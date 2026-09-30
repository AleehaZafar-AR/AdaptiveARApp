# CHANGES.md — Agent ↔ Reviewer handoff

This file is the shared communication channel. **Do not overwrite the Reviewer → Claude
history.** Append new sections; keep older ones intact.

---

## Claude → Reviewer — 2026-09-29 — Quest UX + assembly workflow refinement

### Status legend

| Mark | Meaning |
|---|---|
| **[SV]** | Implemented and **statically verified** (compiles; logic reviewed against real scene data) |
| **[QV]** | **Requires Quest verification** — cannot be confirmed from here |
| **[PC]** | **Requires physical calibration** — numbers are provisional and need the bench |
| **[BLOCKED]** | **Blocked by a missing asset or an undecided procedure** |

Nothing in this pass has run on a headset. Compilation is not validation.

---

### 1. Audit findings that shaped the work

Inspected the live scene, all six StepData assets, StepRunner/StepPresenter, validation,
every participant canvas, ghosts, the physical component hierarchy, Meta Interaction
configuration and `docs/UIReference`.

**Root cause of the stale-UI class of bugs.** There was no single owner of presentation
state. `StepManager` wrote anchoring messages into the *step title*; `StepPresenter` wrote
title/body; `StatusHud` and `TaskListHud` each kept their own counters. A state could end
without anything clearing what it had written. This is why "Block placed." and "Demo" text
survived, why progress disagreed with reality, and why L3 text collided with other fields.
Fixed structurally (§3, §5), not by patching each symptom.

**Assembly hierarchy — what actually exists:**

| Object | Contents |
|---|---|
| `Components/` | crankshaft, camshaft, piston001–004, engineBlockSep001–004 |
| `piston00N` children | PistonHead, ConnectingPin, PistonEnd, ConnectingRod, **PistonNut, pistonBolt, PistonNutOther, pistonBoltOther** |
| `Ghosties/oilPan` children | oilPan mesh, **crankHolder001–004**, oilPanCap, **crankHolderBolt002–016** |

**Consequences, and they are significant:**

- **[BLOCKED] Fasteners are not separate objects.** `crankHolderBolt002–016` and
  `crankHolder001–004` are baked sub-meshes *inside the oilPan ghost*. `pistonBolt` /
  `PistonNut` are child meshes of each piston. None can be picked up or placed
  independently. A real fastening substep needs these extracted into separate movable
  GameObjects with their own ghost targets.
- **[BLOCKED] No tool model exists anywhere in the repository.** The only hits for
  wrench/screwdriver/tool/drill are `OVRPlatformToolSettings.asset` and a stray
  `Drill.mat` from a sample — no mesh. As instructed, I did **not** build a procedural
  placeholder.
- **The trays are empty meshes** with no children, so the "hardware tray with fasteners"
  visible in the headset is the oilPan's baked bolt geometry, not separate parts.

---

### 2. Architecture changes

**New: major stage → action (substep) model** — `AssemblyAction.cs`, `WorkflowState.cs`

- `StepData` (a stage) now carries `List<AssemblyAction> actions`.
- `ActionKind`: `Acknowledge`, `Place`, `Fasten`, `ToolAction`.
- Each action owns its own instruction, part keys, target key, tolerances and ghost keys.
- Actions can be **`enabled = false` with a `disabledReason`** — designed but not
  performable. They are skipped at runtime and the reason is logged, rather than being
  silently absent or blocking the participant on something impossible.

**New: one source of truth for progress** — `WorkflowState`

- Owns phase, stage index, action index, and the completed-action/stage sets.
- `OverallProgress` is **derived** from validated actions; it is never assigned.
- Every display subscribes to its single `OnChanged` event. No component keeps a private
  counter any more. **[SV]**

**New: reusable locking** — `PlacementLock.cs`

- One mechanism for every component: snap to target, freeze physics, disable the ISDK grab
  components, keep the pose relative to `EngineAnchor`.
- ISDK components are found **by namespace** (`Oculus.Interaction.*`) rather than by
  concrete type, because the grab setup differs between the crankshaft and the pistons and
  hard-typing would break on an SDK update. **[SV]**

**Preserved untouched:** `SessionLogger`, `IDecisionProvider`/`DecisionContext` seam,
ArUco anchoring (`ArUcoMarkerTracking`, `ArUcoTrackingAppCoordinator`), `MarkerAnchor` /
`CalibrationOffset` / `EngineAnchor` chain, `SupportLevelController`, B/Y support switching.

---

### 3. UI state lifecycle — the stale-content fix

`ParticipantCard.cs` now owns **every** field on the instruction card. Entering an action:

1. clears all card fields in one place;
2. writes only the new action's content;
3. reads progress from `WorkflowState` (never increments anything itself);
4. re-arms ghosts/arrow through the validator;
5. updates control availability;
6. logs `action_enter`.

`StepManager` no longer writes into the step title — it writes to a dedicated status line
which is hidden permanently once the first action is presented. **[SV]** / **[QV]** that no
stale text survives in practice.

---

### 4. Workflow content authored

Each of the six stages now has four actions:

| Action | Kind | State |
|---|---|---|
| `…​.locate` | Acknowledge | enabled |
| `…​.place` | Place | enabled, validated |
| `…​.fasten` | Fasten | **disabled** — no separately movable fastener |
| `…​.tighten` | ToolAction | **disabled** — no tool model |

The disabled actions are authored deliberately so the intended procedure is visible in the
data and the log shows what was skipped and why. **[BLOCKED]** on the two asset gaps above.

---

### 5. Validation-gated progression

- `AssemblySessionController.CanAdvance` gates the Next control. A `Place` action requires
  a validated placement; `Acknowledge` does not.
- Pressing Next when blocked does **not** advance `StepRunner`; it logs `advance_blocked`
  and surfaces `BlockedReason`.
- `ParticipantCard` sets `nextButton.interactable` from `CanAdvance` and greys the label,
  so the button never implies an action that is unavailable.
- Researcher `SkipStep()` deliberately bypasses the gate. **[SV]** / **[QV]**

**Rejection reasons are now specific** — `RejectReason.WrongComponent` / `TooFar` /
`WrongRotation`, surfaced as "That is not the right component" / "Move it closer to the
highlighted target" / "Rotate the part to match the target". Logged with the attempt. **[PC]**
the distance thresholds separating those reasons.

---

### 6. Interaction fixes

- **Interchangeable pistons.** A piston action accepts any unplaced piston. Whichever one
  the participant actually moves is the one judged; a placed piston is not offered again.
  This was also a *measurement* bug: reaching for an identical piston was being recorded as
  a placement **error**, polluting the error counts that feed the decision layer. **[SV]**
- **All parts grabbable.** `InteractionAndGuidanceSetup` adds Rigidbody + convex mesh
  colliders on child meshes + `DropIntoTray`, and clones the crankshaft's working ISDK block
  onto each part with references retargeted. **[QV] — this is the least certain item in the
  pass.** See §10.

---

### 7. Ghosts

`GhostTarget.mat` — semi-transparent **cyan** (alpha 0.18) with emission so the silhouette
survives bright passthrough. Cyan not green, because green is reserved for confirmed
states. It must read as a placement target, not a replacement part. **[PC]** the alpha:
raise if too faint on the bench, lower if it still reads as a real component.

---

### 8. Spatial placement

`PanelRig` now sits **0.20 m above and 0.10 m beyond** the marker (was 0.42 / 0.14) so a
seated user reads it by eye movement rather than a head tilt. It **locks once the marker is
found** — the marker is taped to the bench, so a fixed position is calmer than one that
re-settles. Left thumbstick click forces a recentre. **[PC]** both offsets.

---

### 9. Logging

Existing JSONL schema and envelope preserved; all AI and physiology fields untouched. Added
events: `onboarding_enter`, `onboarding_complete`, `assembly_start`, `action_enter`,
`action_complete`, `component_grabbed`, `component_released`, `component_locked`,
`help_requested`, `fastener_action`. `validation_attempt` gained `reject_reason`.
`action_enter` records `action_enabled` and `disabled_reason`, so a run's log shows exactly
which substeps were skipped for want of assets. **[SV]**

---

### 10. What I could not verify, and what I need

**[QV] Requires Quest verification**

1. **Whether the cloned ISDK grab block actually works on the pistons.** Generic reference
   retargeting is reasonable but unproven. If a piston still will not grab, that is where it
   failed — tell me and I will inspect what the clone produced rather than guess again.
2. Whether convex hulls on piston sub-meshes feel right, or collide with air near the bores.
3. That no stale text survives an action transition in practice.
4. That the gated Next reads as intentional rather than broken.
5. Panel legibility and whether the lowered rig is genuinely comfortable seated.

**[PC] Requires physical calibration**

- Position/rotation tolerances (4 cm / 25°) — **provisional, not validated thresholds**.
- Settle duration, rest-accept duration, handled-move threshold.
- Ghost alpha; rig height/depth; the error distance that distinguishes "wrong component"
  from "too far".

**[BLOCKED] Needs you**

1. **A tool model** (wrench / ratchet / powered driver). Architecture and `ToolAction` are
   in place; assign a prefab and a detection rule and the substeps activate.
2. **A decision on fastener extraction.** To make fastening real, `crankHolderBolt002–016`,
   `crankHolder001–004` and the piston bolts/nuts must become separate movable GameObjects
   with ghost targets. That is destructive surgery on the model hierarchy — I have **not**
   done it unasked. Say the word and I will, or supply a model with separated fasteners.
3. **The actual fastener sequence.** Which bolts, in what order, to what stage. I have not
   invented one.

**Unresolved research decisions** (unchanged from the previous pass)

- `taskComplexity` is 0 for every stage — a decision-layer input I will not invent.
- All stages open at L2 — a starting choice, not a transition policy.
- L3 audio exists only for the crankshaft; the other five have no clip.

---

### 11. Honest assessment against your acceptance criteria

Met and statically verified: state clearing, validation gating, locking, authoritative
progress, substep workflow, onboarding, specific feedback, ghost material, logging.

Met in code but unproven on device: all-parts-grabbable, comfortable positioning, arrow
usefulness, no-overflow.

**Not met:** fastener and tool substeps are architecture only — they are authored,
disabled, and logged as skipped. The camshaft stage therefore does **not** yet include a
working holder/fastening sequence, because the holders are baked geometry. That is the
single largest gap between this build and your stated end state, and it is an asset problem
rather than a code one.


---

## Reviewer → Claude — 2026-09-29 — DEVICE REVIEW: functional recovery + deterministic UI pass

### Read this before doing more work

Commit `836d792` was tested on Quest. The build is still **not usable enough for participant testing**. Treat the device evidence below as authoritative over static verification. Do not spend this pass on AI, BLE, fastener extraction, tool models, or expanding the workflow. First make the existing core interaction loop actually usable on Quest.

This is **not primarily a visual-polish request**. The highest-priority failures are functional/spatial interaction failures.

### Quest-observed failures from the latest run

1. **Text still overlaps / escapes its intended layout.** Some labels are dramatically larger than their controls and extend outside the button/card.
2. **Participant panels jitter when the user gets close to the marker/workspace.** The UI must not inherit noisy marker pose updates or fight tracking corrections during use.
3. **Buttons are frequently practically unreachable/unpressable in the headset.** A button that looks correct but cannot be comfortably selected is a failed control.
4. **Ghosts still read as solid virtual parts rather than transparent placement targets.**
5. **Directional arrows are oriented incorrectly / do not reliably communicate the target.**
6. **Only the crankshaft is reliably grabbable. Other required assembly objects still cannot be picked up.** The previous cloned-ISDK strategy therefore failed device validation.
7. Overall participant usability is currently unacceptable even though several systems compile and are marked [SV].

Do **not** respond to these failures by merely changing constants and declaring them fixed. Trace the runtime ownership, transforms, ray/poke interaction, material/shader behavior, and actual ISDK configuration that produce them.

### P0 — FUNCTIONAL RECOVERY. Do this before visual polish.

#### A. Make every currently-required movable component actually grabbable

The crankshaft is the known-good reference. The pistons/camshaft/other current placeable components are not.

Do not clone the crankshaft interaction hierarchy blindly again.

For each required movable component:
- inspect the actual crankshaft ISDK setup and object hierarchy;
- inspect the candidate object's hierarchy, colliders, Rigidbody, scale, layers, interaction groups and grab interactable/transformer references;
- determine exactly why the candidate is not selectable/grabbable on Quest;
- create a reusable setup only after understanding the difference;
- ensure colliders correspond to the physical mesh closely enough for hand/controller interaction;
- ensure Rigidbody and interaction components live at the hierarchy level expected by ISDK;
- ensure references point to the candidate object, not copied crankshaft transforms;
- ensure placement/locking later disables interaction cleanly.

Do not mark “all parts grabbable” [SV]. It remains [QV] until device tested.

Current acceptance target for this pass: crankshaft + every enabled Place action's component can be picked up, moved and released. Fasteners/tool actions remain disabled for now.

#### B. Stabilize the participant UI in space

The latest run still shows panel jitter near the marker/workspace.

The participant UI must **not continuously follow raw ArUco marker pose** after calibration. Separate:
- assembly/world registration, which may be anchored from marker calibration;
- participant HUD/card pose, which should become stable after onboarding/calibration.

After the workspace is accepted:
- capture a stable presentation pose/reference;
- stop applying frame-to-frame marker corrections to the participant card;
- do not parent the card under a transform that continues to receive noisy marker smoothing/corrections if that produces visible jitter;
- allow explicit recenter only through the intended recenter action;
- preserve EngineAnchor behavior for assembly content.

Inspect the transform chain rather than assuming the existing “lock once found” code is effective.

#### C. Fix interaction reachability

The user must be able to activate every participant-facing button from the normal seated working pose without leaning/stretching into the panel.

Audit the actual Meta ray/poke setup, canvas/world-space interaction, colliders/raycast targets and panel distance.

Prefer **ray interaction for the primary card controls** if poke requires physically reaching too far. Do not require the participant to touch a world-space panel that is intentionally positioned beyond the assembly.

Minimum target sizes must be generous for Quest. All controls need a clear visual hit area and a matching interaction hit area.

Validate that Back / Next or Continue / Help / Replay / Begin / Recenter, if present, use the same reliable interaction approach.

#### D. Ghost material must be genuinely transparent on Quest

The current ghost still appears solid.

Do not assume `alpha = 0.18` means transparency. Verify the actual URP material/shader configuration:
- Surface Type = Transparent or equivalent runtime shader state;
- appropriate blend mode;
- alpha respected by shader;
- depth/write behavior appropriate for a ghost overlay;
- no emission value that visually overwhelms transparency;
- no runtime material replacement restoring an opaque material.

Desired result: the real environment/engine is clearly visible **through** the ghost while the ghost silhouette remains readable. Cyan/teal outline/tint is fine. It must unmistakably read as a target volume, not a second solid component.

#### E. Replace arrow “orientation guesses” with target-derived geometry

Do not use hand-authored Euler rotations as the primary orientation mechanism.

For a directional placement cue:
- define a source position and target position;
- place the arrow between/near those anchors as appropriate;
- orient its forward axis from the source toward the target using the prefab's actual local forward axis;
- account for the arrow mesh's authored axis once, centrally;
- keep any per-action offset small and explicit;
- parent/resolve positions in the same coordinate space before computing direction.

If an arrow cannot point correctly for an action, hide it rather than display misinformation.

### P1 — REPLACE THE PARTICIPANT UI WITH ONE DETERMINISTIC CARD

Use the supplied inspiration image as **visual language only**, not as a requirement to recreate its multiple-panel composition. Our participant UI should be simpler.

There should be **one primary participant instruction card**. Remove/hide the permanent participant-facing Steps and Status panels. Research/debug information belongs in a separately toggled researcher HUD.

#### Card layout specification

Build one world-space card with a stable fixed layout. Do not use content-driven geometry that can collapse, expose an uncovered dark region, or allow text to escape.

Suggested starting physical size (make serialized/configurable):
- width: ~0.42–0.48 m
- height: ~0.25–0.30 m
- comfortable viewing distance: ~0.75–1.0 m from seated head pose
- position: centered or slightly left of center, just above the assembly's highest normal manipulation volume
- never require repeated upward neck tilt

Visual language from the inspiration:
- charcoal/near-black semi-opaque card;
- subtle cyan/teal accent;
- white primary text;
- muted grey secondary text;
- amber only for correction/warning;
- restrained green only for confirmed success;
- thin borders/dividers;
- generous internal padding;
- no neon blocks and no debug aesthetic.

#### Strict hierarchy

Inside the single card, top to bottom:

1. **Header:** “V8 ASSEMBLY” small/medium.
2. **Progress:** compact segmented or linear progress + concise stage/action count.
3. **Action title:** e.g. “Position the crankshaft”.
4. **Instruction body:** ONE current actionable instruction. Maximum 2–3 rendered lines.
5. **Context feedback area:** normally empty; shows one concise validation message when needed.
6. **Optional contextual action:** Help / Replay demo only if content exists.
7. **Bottom controls:** Back + Continue/Next, consistently sized.

Do not show long L3 paragraphs. L3 decomposition advances through sub-actions/instruction states one at a time.

#### Text/layout rules — mandatory

- Use TextMeshPro auto-size only within a conservative bounded range, not an unlimited shrink/grow behavior.
- Set explicit RectTransforms for title/body/feedback/buttons.
- Enable wrapping where intended.
- No overflow mode that renders outside the card.
- Button label font size must be explicitly bounded and substantially smaller than the button height.
- Button label RectTransform must remain inside the button with padding on all sides.
- All card text must have a maximum rendered region.
- If content exceeds the region, fix/split the content; do not allow it to spill.
- Background must cover the **entire card bounds**. There must never be an exposed lower section because content/background heights disagree.
- One owner (`ParticipantCard`) controls visible participant content. No other runtime component writes directly to its TMP fields.

#### State behavior

On every state/action transition:
- clear title/body/feedback/context action;
- stop old demo/audio;
- hide old ghost/arrow;
- then populate the new state.

There should never be two instructions occupying the same field or old text visible underneath new text.

### P2 — COMPLETE THE CORE PHYSICAL LOOP

For each currently enabled Place action the user experience must be:

**instruction → grab component → manipulate → release → validate → correction OR success → snap → lock → clear ghost/arrow → progress update → next action**

Requirements:
- Next/Continue cannot advance an incomplete Place action.
- Successful placement locks the actual object and prevents re-grab.
- Progress derives only from validated completion.
- A failed attempt does not increment completion.
- Switching support levels must not reset completion or create duplicate presentation objects.
- Entering the next action clears every previous guidance artifact.

Do not touch fasteners/tool actions in this pass; they can remain explicitly disabled. We first need one reliable physical interaction pipeline.

### Onboarding behavior

Keep onboarding short and stable:
1. Welcome / task purpose.
2. Major task overview.
3. Explain that transparent cyan placement guides show targets and the system confirms completion.
4. Begin Assembly.

No task ghost or arrow should appear until **Begin Assembly** is activated.

### Support-level presentation

The physical success criteria remain identical across L1/L2/L3.

- **L1:** concise goal; no unnecessary spatial overlay.
- **L2:** concise goal + transparent target ghost/spatial guidance.
- **L3:** one decomposed instruction at a time + transparent ghost + correct directional cue + Help/Replay/audio only when an authored asset exists.

Do not make L3 a paragraph.

### What NOT to work on in this iteration

Do not:
- implement AI provider;
- implement BLE/HRV;
- extract baked fasteners;
- create a tool model;
- expand to a more detailed V8 procedure;
- redesign logging architecture;
- change research claims;
- spend time on decorative animations before P0 works.

Preserve the existing AI seam and logger.

### Required self-check before handoff

Before reporting this pass complete, inspect the scene/code and answer explicitly in Claude → Reviewer:

1. Why were the non-crankshaft objects not grabbable, and what exact configuration difference was fixed?
2. What transform was causing/allowing participant-card jitter, and how is the card now decoupled after calibration?
3. Which Quest interaction path activates participant buttons (ray/poke), and why should it be reachable from the seated pose?
4. Which shader/material settings now make the ghost truly transparent rather than merely assigning a low alpha value?
5. How is arrow orientation computed from source/target positions, including the prefab's local forward axis?
6. What hard bounds prevent title/body/button text from rendering outside its allocated RectTransform?
7. Which component is the sole runtime writer of participant-facing text?

If any answer is uncertain, label it [QV] rather than presenting it as solved.

### Next Quest acceptance test

The next device build is successful only if the user can demonstrate, in one continuous run:

1. anchored workspace;
2. stable onboarding UI with no jitter;
3. comfortably reachable Begin button;
4. no ghost before Begin;
5. first instruction with no overlap/overflow;
6. transparent crankshaft target;
7. crankshaft can be grabbed;
8. incorrectly placed crankshaft is rejected without advancing;
9. correct placement validates, snaps and locks;
10. progress updates once;
11. old text/ghost/feedback clears;
12. first piston can actually be grabbed;
13. piston target ghost is transparent;
14. directional cue points at the actual target;
15. Next remains gated until piston placement succeeds.

**Do not call the iteration complete based on compilation. The purpose of this pass is to produce a build worth testing against these 15 concrete device criteria.**
---

## Claude → Reviewer — 2026-09-29 (b) — Fastener extraction and tool architecture

Proceeding on the blockers with stated assumptions, as agreed. No Reviewer → Claude
section existed in this file at the time, so this continues from the previous handoff.

### Fastener extraction — the blocker is now removed

`AdaptiveAR ▸ Fasteners ▸ 1 Validate / 2 Extract / 3 Author`

The 4 bearing caps and 15 bolts were baked sub-meshes inside `Ghosties/oilPan`, drawn as
though already fitted and impossible to pick up. Extraction now:

- moves each one out to `Ghosties/Fasteners`, keeping its world pose — that pose **is** the
  correct assembled position, so it becomes the ghost **target**;
- duplicates it into `Components/Fasteners` as the movable **part**, staged in a grid near
  the tray, with its real materials restored;
- registers `ghost.*` and `part.*` keys for both.

`oilPan_lambert2_0` and `oilPanCap_lambert2_0` are untouched, so the oil pan keeps its
lambert2 materials as you asked. **[SV]** structurally; **[QV]** that the staged positions
are actually reachable on the bench.

### Fastener sequence — derived, not invented

Every bolt is assigned to the nearest cap along the crankshaft axis. Computed from the
model:

| Cap | z | Bolts |
|---|---|---|
| crankHolder001 | +57.87 | 002, 003, 004 |
| crankHolder002 | +29.34 | 005–008 |
| crankHolder003 | +0.01 | 009–012 |
| crankHolder004 | −28.85 | 013–016 |

Three on the first cap because the model has no `Bolt001`. The dry run recomputes this from
the live scene rather than trusting the table above.

### A scoping decision you should know about

15 individual bolts would add ~19 validated placements to the crankshaft stage and push a
run far past the 15-minute target. So **the 4 caps are enabled and the individual bolts are
authored but disabled**, with that reason stored in the asset and written to the log. Flip
`enabled` on those actions for a full-procedure run. This is a judgement call about session
length, not a claim about the procedure.

### Camshaft — an honest correction

The brief asked for camshaft holders and fasteners. **This model has no camshaft-specific
holders.** `crankHolder001–004` sit at the crank journals, so they belong to the crankshaft
stage, and that is where they are authored. The camshaft stage remains place-and-validate.
**[BLOCKED]** on a model that includes camshaft caps.

### Tool architecture — real, still unusable

`ToolInteraction.cs` implements four switchable detection rules: proximity dwell, proximity
+ trigger held, proximity + accumulated rotation, proximity + single press. Which one
survives hand-tracking on a Quest cannot be decided from here, so all four ship and the
question is settled by testing. Completing a tool action advances the substep exactly as a
placement does, and writes `fastener_action`.

**[BLOCKED]** Assign a tool model to `toolTip` and the `ToolAction` substeps become
performable. Until then they stay disabled and are logged as skipped.

### taskComplexity — now populated, with its basis stated

Was 0 everywhere, which gave the decision layer nothing. Now a **structural ordinal**:
`1 base + 1 if the part is unique rather than a repeat + 1 if the stage has fastening`.
Crankshaft 3, piston 1 → 2, pistons 2–4 → 1, camshaft 2. It counts what the procedure
demands and says nothing about the operator. Replace it when the literature supplies a
scale. **Not** a difficulty or workload rating.

### Run order

```
Fasteners   ▸ 1 Validate  →  2 Extract  →  3 Author Fastening Substeps
Assembly    ▸ 2 Apply Full Assembly Content
Interaction ▸ 1 Apply Ghost Target Material  →  3 Wire Guidance Arrow
UI          ▸ 2 Apply UI Restyle
```
Save the scene. Skip `Interaction ▸ 2` — that is the one that would overwrite your manual
grab configuration.

Note: after extraction the fastener parts need grab components. Either run `Interaction ▸ 2`
once and accept it touching every part, or configure the new `Components/Fasteners` children
by hand as you did for the others.

### Still open

- A tool model.
- A model with camshaft caps, if stage 3 is to include fastening.
- Whether the extracted caps are physically graspable at their real size — they are small,
  and hand tracking may struggle. **[QV]**
- All tolerances remain provisional. **[PC]**

---

## Claude → Reviewer — 2026-09-29 (c) — Functional recovery pass

Responding to the device review. Scope respected: **no** AI, BLE, fastener extraction, tool
models or workflow expansion in this pass.

**Note on ordering.** The fastener/tool commit landed before your review was visible to me.
Nothing in it runs automatically — those are opt-in menu items under `AdaptiveAR ▸ Fasteners`.
**Do not run them this pass.** They are left in place for later.

### Required self-check

**1. Why were the non-crankshaft objects not grabbable, and what was fixed?**

Measured from the scene, not assumed:

| Part | Rigidbodies in subtree | Colliders owned by the part's own Rigidbody |
|---|---|---|
| crankshaft | 1 | 1 |
| camshaft | 1 | 1 |
| piston001–004 | **9** | **0** |

A collider in Unity belongs to its **nearest Rigidbody ancestor**. Each piston had a
Rigidbody on the root *and* on all eight child meshes, so every collider belonged to a child
body and the root Rigidbody — the one `Grabbable` and the interactables reference — owned
**nothing**. There was no hit target that resolved to the grabbable body, so it could never
be selected however correct its references were.

I also verified the ISDK `_rigidbody` references: **all correct**, pointing at each part's
own body. My earlier "the clone mis-targeted references" theory was **wrong**.

Fix: `AdaptiveAR ▸ Grab ▸ 1 Diagnose / 2 Repair` — one Rigidbody at the part root, nested
ones removed, all mesh colliders convex, duplicates dropped (`ConnectingPin` had two).
**[QV]** — only a Quest run proves a hand can select them.

**2. What caused the card jitter, and how is it decoupled now?**

`PanelRig` was **parented to `MarkerAnchor`**, which ArUco rewrites every frame. The previous
"lock" only skipped the rig's own follow computation — a child inherits its parent transform
regardless, so every pose correction and all tracking noise still reached the panels. Locking
local logic could not possibly have fixed it.

Fix: on anchor lock the rig now **unparents itself** (`DetachFromMarkerChain`), keeping its
world pose and caching the marker position it still needs. Nothing downstream of the marker
can move it afterwards. `EngineAnchor` stays in the marker chain, so the assembly keeps its
registration — only the participant UI is decoupled. **[QV]**

**3. Which interaction path activates the buttons, and why is it reachable?**

**Ray**, via the existing `ISDK_RayCanvasInteraction` + `PointableCanvas` + `RayInteractable`
on the canvas, whose `Surface` is resized with the card so the ray target matches the visible
bounds. Controls are 170 × 56 mm with `raycastTarget` on the button's own Image, so the hit
area is exactly the visible area. Poke is not required and the card is not positioned to be
touched. **[QV]** — that the ray actually reaches it from the seated pose is a device question.

**4. What makes the ghost genuinely transparent now?**

Three things, all of which were wrong before:

- **Shader: URP/Unlit, not Lit.** A Lit ghost is shaded by scene lighting and reads solid at
  any alpha. Unlit stays flat.
- **Emission removed.** It was `cyan × 0.55` with `_EMISSION` enabled — emission adds light
  *on top of* the blend, which is precisely "reads as a solid glowing part".
- **Alpha clipping explicitly off** (`_AlphaClip = 0`, `_ALPHATEST_ON` disabled) plus
  `_Surface = 1`, SrcAlpha/OneMinusSrcAlpha, `_ZWrite = 0`, `DepthOnly` and shadow passes
  disabled, render queue 3000. Alpha is 0.25, green as requested.

Also fixed the reason the earlier colour change silently did nothing: `EnsureGhostMaterial`
returned an existing asset untouched once created. Appearance is now reapplied every run.
**[QV]**

**5. How is arrow orientation computed?**

From geometry: `direction = focus.position − arrow.position`, then
`Quaternion.LookRotation(direction, up)` with a non-parallel up vector, multiplied by a
**single central axis correction** (`meshForward`, default `MinusY`) that maps the arrow
mesh's authored axis onto +Z. A per-action nudge exists but defaults to zero. The previous
hard-coded `LookRotation(Vector3.down, Vector3.forward)` is gone. If no usable direction
exists the arrow hides rather than pointing somewhere misleading. **[QV]** — `meshForward`
may need one change once the prefab's real axis is observed.

**6. What prevents text rendering outside its RectTransform?**

- Every row has an **explicit RectTransform with a fixed height**; nothing is content-driven,
  so content cannot move or resize a row.
- Auto-size is **bounded** per field (title 22–30, body 14–19, feedback 13–17, button label
  16–20) with `overflowMode = Truncate`. It shrinks to the floor, then truncates.
- Button labels are capped at 20 against a 56 mm button and inset 12 × 8 on all sides.
- The card background is anchored to the **full canvas bounds** (stretch, zero offsets), so
  it cannot disagree with content height and leave an uncovered region.

**7. Which component is the sole runtime writer of participant text?**

`ParticipantCard`. `StepManager` writes only to a separate anchoring status line, hidden
permanently once the first action appears. `StepPresenter` no longer drives the card.
`StatusHud` and `TaskListHud` live on canvases that are now **hidden**.

### Also done

- **One card only.** `AdaptiveAR ▸ UI ▸ 3 - Build Participant Card` builds it and hides
  `OverviewCanvas` and `StatusCanvas`. Layout: header → progress bar → progress label →
  divider → title → body → feedback → Back / Continue.
- Progress line reads `STAGE 1 / 6   CRANKSHAFT   STEP 2 / 3`, every number from
  `WorkflowState`.

### Run order for the next build

```
Grab        ▸ 1 Diagnose  →  2 Repair      (expect every part to report OK)
UI          ▸ 3 Build Participant Card
Interaction ▸ 1 Apply Ghost Target Material  →  3 Wire Guidance Arrow
```

Save the scene. **Skip `Interaction ▸ 2`** — it would re-add the nested Rigidbodies that
caused the problem — and **skip `Fasteners ▸ *`** this pass.

### Honest status against your 15 criteria

Everything above is **[QV]**. Nothing has run on a headset. The four causes in answers 1, 2,
4 and 5 are structural defects I can point at in scene data rather than constants I adjusted
— but whether fixing them yields a usable build is exactly what the next device run decides.

Largest remaining risk: **criterion 12, the piston grab.** The nested-Rigidbody explanation
fits all the evidence, but if pistons still cannot be picked up after the repair, the cause
is elsewhere in the ISDK configuration, and the Quest-side symptom would narrow it fast —
specifically whether the part shows a hover highlight but refuses to grab, or shows no
highlight at all.


---

## Reviewer → Claude — 2026-09-29 — CORRECTION: adaptive multi-panel UI + real piston assembly

### Context and priority

Keep the useful functional-recovery fixes from the latest pass: root-Rigidbody/collider repair, UI decoupling from MarkerAnchor after calibration, ray-based controls, real URP transparency, target-derived arrows, bounded TMP layout, validation gating, snap/lock, WorkflowState progress, logging and the AI seam.

The user has NOT run most of the latest Editor apply/setup commands yet. Update the tooling now so she does not apply an obsolete UI/workflow and then undo it.

Two assumptions are corrected:

1. Do NOT reduce the application to one participant card at all support levels.
2. Do NOT treat each piston as a preassembled parent whose only task is placement into the engine.

Functional Quest usability remains P0. UI adaptation is built on top of a working grab/place/validate/lock loop.

### 1. Adaptive multi-panel participant UI

The interface should be capable of changing information density and task focus across support levels. Do not encode or claim that a particular panel “causes stress.” Implement the behavior neutrally as peripheral information being visible, reduced, faded, collapsed or hidden.

Keep the CENTER of view open for the physical engine, target ghost, arrow and local validation cue.

Use up to four restrained zones:

**Panel 1 — Instruction / Guidance (primary, left)**
- current major stage/action;
- concise instruction;
- validation feedback;
- Help/Replay only when authored;
- Back/Continue where appropriate.
- Remains at every support level.

**Panel 2 — Steps / What's Next (secondary)**
- compact completed/current/upcoming overview;
- enough context to know where the participant is and what comes next;
- no duplicate instruction paragraphs.

**Panel 3 — Performance (secondary)**
- restrained participant-visible metrics already available, such as overall progress, elapsed task time and/or attempt/error feedback;
- do not invent metrics;
- not a debug dump.

**Panel 4 — Research/System (tertiary)**
- support level, validation/decision diagnostics and future AI/physiology fields;
- primarily hidden, researcher-toggleable, or extremely low salience/faded;
- must never compete with the task.

Use the supplied inspiration's visual language: charcoal translucent cards, cyan/teal guidance accent, white primary type, muted secondary type, amber correction, restrained green success, consistent padding/borders. Do NOT resurrect the old giant Overview/Status appearance.

#### Support-level presentation

**L1 — minimal task assistance / information-rich context**
- concise goal/instruction;
- Steps/What's Next visible;
- Performance visible;
- Research/System hidden or extremely subdued;
- minimal task-specific spatial assistance.

**L2 — guided**
- primary instruction remains;
- Steps remains but can condense/reduce salience;
- Performance remains but lower salience;
- transparent target ghost/spatial cue available;
- Research/System hidden/subdued.

**L3 — assisted/focused**
- richer IMMEDIATE task assistance while reducing peripheral information;
- Steps collapses/fades out;
- Performance collapses/fades out;
- Research/System hidden;
- strong primary instruction card;
- optionally a small contextual Parts card if useful;
- one decomposed actionable instruction at a time;
- transparent ghost + correct directional cue;
- Help/Replay/audio only where authored.

Design principle: **higher support = richer immediate task guidance, not more simultaneous screen information.**

Transitions should be calm fades/collapses. Support switching must not reset workflow state, duplicate canvases or leave stale text.

#### Supersede the one-card builder

The current `UI ▸ 3 - Build Participant Card` hides OverviewCanvas/StatusCanvas and creates “one card only.” Revise/supersede it BEFORE the user runs it.

Provide ONE idempotent current UI build/apply path for the adaptive multi-panel system. Running it twice must not duplicate objects/components. Explicitly mark the old one-card command obsolete if its behavior remains in code.

All TMP fields remain hard-bounded: fixed RectTransforms, bounded autosize, wrapping only where intended, truncate/split rather than render outside bounds. Button label regions stay inside button bounds. Ray hit areas must match visible controls. Participant UI remains decoupled from continuously corrected MarkerAnchor after calibration.

### 2. Ghost visual correction

The latest handoff says alpha 0.25, **green**. Change active target ghosts to the inspiration's **cyan/teal**. Green is reserved for confirmed/completed success.

Keep the technically correct transparency setup: URP transparent surface, alpha blend, alpha clip off, appropriate ZWrite/depth/shadow behavior, and no emission that defeats transparency. The real engine/environment must be clearly visible THROUGH the ghost on Quest.

### 3. Real piston workflow: assemble it first

The intended task is NOT “grab complete piston001 and insert it.”

The hierarchy audit already identified:
- `PistonHead`
- `ConnectingRod`
- `ConnectingPin`
- `PistonEnd`
- `pistonBolt`
- `PistonNut`
- corresponding “Other” fastener meshes.

For each piston, the user's intended procedure is:

1. **Piston head** — identify/grab the piston head.
2. **Connecting rod** — identify/grab and position the connecting rod relative to the piston head.
3. **Join head + rod** — connect them using the intended connecting bolt/pin hardware.
4. **Attach rod end/cap** — attach `PistonEnd` to the connecting rod using the intended bolt/nut hardware.
5. **Install completed piston assembly** — place the assembled piston into the correct engine location, validate, snap/lock.

Do NOT invent exact bolt counts, torque, threading direction, or finer mechanical sequence not established by the model/user.

#### Runtime architecture

The piston parent cannot be the sole manipulation unit if its children are assembled independently.

Use a NON-DESTRUCTIVE strategy:
- preserve original imported/source assets;
- create derived runtime prefabs/duplicates if necessary;
- make PistonHead, ConnectingRod, ConnectingPin/bolt, PistonEnd and required fasteners independently interactable;
- give required joins explicit target transforms/ghosts;
- after a child is correctly joined, lock/parent it into the growing piston assembly while preserving the next interaction;
- after assembly is complete, allow the completed piston assembly to be manipulated as the unit for final engine placement.

Do not destructively edit the only source model/prefab.

If a required fastener is genuinely inseparable, report the exact blocker. The audit says piston fasteners are child meshes, so verify whether they can become independent runtime objects before calling the piston sequence blocked.

### 4. Piston workflow and support granularity

All support levels require the SAME physical completion.

- **L1:** broader goal-level guidance where feasible, with contextual panels visible.
- **L2:** current assembly action + relevant target/ghost.
- **L3:** physical sequence exposed one actionable substep at a time (head → rod → connector → end/cap + fasteners → installation), while peripheral panels fade/collapse and immediate spatial guidance strengthens.

L3 must not be a longer paragraph.

The exact L1/L2 grouping must remain validation-safe: downstream progression cannot occur until physical prerequisites are actually completed.

### 5. Keep functional recovery as P0

Do not regress the required physical loop:

**instruction → grab → manipulate → release → validate → correction OR success → snap/lock → clear guidance → progress update → next action**

The next build must specifically prove:
- crankshaft grab/validate/lock;
- independent piston-head grab;
- independent connecting-rod grab;
- first piston join interaction as far as implemented;
- gated progression;
- no stale text;
- stable non-jittering panels;
- reachable ray buttons;
- transparent cyan ghost;
- correctly oriented target-derived arrow.

Fastener/tool architecture may remain opt-in/blocked where assets or procedure are genuinely missing. Do not let camshaft asset gaps block this iteration.

### 6. Current run-order requirement

Because the user has not run most previous menu operations, finish by giving ONE clean current Unity menu run order.

Explicitly state:
- which commands to run;
- which old commands are obsolete and must NOT be run;
- whether any new setup command supersedes `UI ▸ 3 - Build Participant Card`;
- whether existing Fastener/Interaction tools should remain skipped.

Do not make the user reconcile contradictory historical run orders herself.

### 7. Next handoff

In Claude → Reviewer report:
- what changed relative to the functional-recovery pass;
- L1/L2/L3 panel visibility/transition behavior;
- ownership/lifecycle of each panel;
- how stable positioning and ray reachability are preserved;
- how piston source hierarchy became independently manipulable without destroying source assets;
- which piston substeps are functional vs blocked;
- exact current menu run order;
- obsolete commands NOT to run;
- [QV] Quest checks;
- [PC] physical calibration items.

Continue autonomously on reversible implementation work. Stop only for a genuine missing physical/procedural fact, destructive source-asset operation, or research-methodology decision.
