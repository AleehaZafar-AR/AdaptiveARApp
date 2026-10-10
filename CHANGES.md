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

---

## Claude → Reviewer — 2026-09-30 — Adaptive multi-panel UI + real piston assembly

Implements the correction. All functional-recovery work from the previous pass is intact:
root-Rigidbody/collider repair, PanelRig decoupling, ray controls, URP transparency,
target-derived arrows, bounded TMP, validation gating, snap/lock, WorkflowState progress,
logging, AI seam.

### 1. What changed relative to the functional-recovery pass

The one-card direction is superseded. `UI ▸ 3` is renamed **`UI ▸ OBSOLETE - 3 Build Single
Card (do not run)`** and now shows a confirmation dialog explaining what replaced it, so it
cannot be run by accident. Its card-building code was extracted into
`ParticipantCardSetup.BuildInstructionCard()` and is reused — the card itself was fine; what
was wrong was hiding the context panels.

`UI ▸ 4 - Build Adaptive UI (current)` is the single current UI path.

### 2. L1 / L2 / L3 panel behaviour

| Zone | Position | L1 | L2 | L3 |
|---|---|---|---|---|
| 1 Instruction | left | Full | Full | Full |
| 2 Steps / What's Next | right | Full | Reduced | Hidden |
| 3 Performance | right, below | Full | Reduced | Hidden |
| 4 Research / System | far right, low | Faded | Faded | Hidden |

`Reduced` = 55% alpha, still interactive. `Faded` = 18%, **not** interactive. `Hidden` = 0%
and the GameObject switched off.

Higher support therefore means **richer immediate guidance and less competing peripheral
information**, which is the stated principle. The implementation is neutral: it decides what
is shown, how brightly, and whether it accepts rays. It encodes no claim about effects on a
participant.

The **centre is empty at every level** — panels flank it so the engine, ghost, arrow and
local validation cue own the middle.

Transitions are alpha fades over 0.35 s. A panel below 50% alpha stops blocking raycasts, so
a faded panel cannot swallow a ray meant for the work behind it. `AdaptivePanelController`
touches visibility only — it never reads or writes workflow state, so switching support
cannot reset progress, duplicate a canvas or clear an instruction.

### 3. Panel ownership and lifecycle

| Zone | Canvas | Sole runtime writer |
|---|---|---|
| 1 | DemoUICanvas | `ParticipantCard` |
| 2 | OverviewCanvas | `TaskListHud` |
| 3 | StatusCanvas | `StatusHud` |
| 4 | ResearchCanvas (created) | `ResearcherHud` |

Presence is owned by `AdaptivePanelController` alone; no HUD changes its own visibility.
Support level is **not** shown on any participant panel — it lives only in zone 4.

### 4. Stability and reachability, preserved

All four canvases are children of `PanelRig`, which still unparents itself from
`MarkerAnchor` on anchor lock, so no zone inherits marker corrections after calibration.
Every canvas keeps its `ISDK_RayCanvasInteraction` block and its `Surface` is resized with
it, so ray targets match visible bounds. Controls remain 170 × 56 mm with `raycastTarget` on
the button's own Image.

### 5. Piston assembly — how it became manipulable without destroying source assets

Non-destructive by construction. For each piston the tool creates **derived duplicates**:

```
Components/PistonKits/PistonKit00N     kit root - the growing assembly
  PistonHead, ConnectingRod, ...       grabbable duplicates, staged in the tray
Ghosties/PistonKits/PistonKit00N       ghost targets at the assembled poses,
                                       positioned at a bench build zone
```

The original `Components/piston00N` is **deactivated, never deleted**. The imported model
and its prefab are untouched; `git checkout` of the scene reverts everything regardless.

`PistonAssembly` owns the change of manipulation unit: as each component is validated and
locked it is parented into the kit (world pose preserved), and when every required component
is in, the kit's own grab components are enabled so the finished piston becomes the thing
being installed. It watches `PlacementLock` state rather than subscribing to the validator,
so it stays correct whatever completed a part and in what order.

Each derived part gets the shape the grab diagnosis proved is required: **one** Rigidbody at
the root, no nested bodies, convex mesh colliders, and a cloned ISDK block with its Rigidbody
references retargeted. Ghost copies have all interaction stripped — a target is never
grabbable.

### 6. Piston substeps: functional vs blocked

Per piston: `locate → head → rod → connecting pin → rod end → retaining bolt → install`.

| Substep | Component | State |
|---|---|---|
| locate | — | enabled |
| head | `PistonHead` | enabled |
| rod | `ConnectingRod` | enabled |
| connecting pin | `ConnectingPin` | enabled |
| rod end | `PistonEnd` | enabled |
| retaining bolt | `pistonBolt` | enabled |
| nut, second bolt, second nut | `PistonNut`, `pistonBoltOther`, `PistonNutOther` | **authored, disabled** |
| install | kit → `ghost.piston00N` | enabled |

The three disabled fasteners are a **session-length** decision, not a procedural claim: six
enabled actions × four pistons is already ~24 placements. The reason is stored in the asset
and written to the log; flip `enabled` for a full-procedure run.

**The piston fasteners are separable** — I verified they are child meshes with their own
mesh filters, so duplicating them into independent objects works. That part of the earlier
audit's "blocked" verdict does **not** apply to pistons. It still applies to the crank
bolts, which are baked into the oil pan.

Nothing about torque, thread direction or bolt count was invented — only the component
meshes the model contains.

### 7. Ghost colour

Active target ghosts are now **cyan** (`0.25, 0.82, 0.85`, alpha 0.25). Green is reserved for
confirmed success. Transparency setup is unchanged from the last pass and still correct:
URP **Unlit**, alpha blend, alpha clip off, `_ZWrite 0`, depth and shadow passes disabled,
**no emission**.

---

## AUTHORITATIVE RUN ORDER — this supersedes every earlier list

Open `Assets/1 - ArUcoMarkerTracking.unity`, then run **in this order**:

```
1.  AdaptiveAR ▸ Grab        ▸ 1 - Diagnose Grabbability (dry run)
2.  AdaptiveAR ▸ Grab        ▸ 2 - Repair Grabbability
3.  AdaptiveAR ▸ Assembly    ▸ 2 - Apply Full Assembly Content
4.  AdaptiveAR ▸ Piston      ▸ 1 - Validate Piston Kits (dry run)
5.  AdaptiveAR ▸ Piston      ▸ 2 - Build Piston Kits
6.  AdaptiveAR ▸ Piston      ▸ 3 - Author Piston Substeps
7.  AdaptiveAR ▸ Interaction ▸ 1 - Apply Ghost Target Material
8.  AdaptiveAR ▸ UI          ▸ 2 - Apply UI Restyle          (creates PanelRig)
9.  AdaptiveAR ▸ UI          ▸ 4 - Build Adaptive UI (current)
10. AdaptiveAR ▸ Interaction ▸ 3 - Wire Guidance Arrow
```

Then **Ctrl+S**. Steps 1 and 4 change nothing — read their Console output before continuing.

### Do NOT run these

| Command | Why |
|---|---|
| `UI ▸ OBSOLETE - 3 Build Single Card` | Superseded. Hides Steps and Performance. Now behind a confirmation dialog. |
| `Interaction ▸ 2 - Make All Parts Grabbable` | Re-adds the nested Rigidbodies that made the pistons ungrabbable. `Grab ▸ 2` replaces it. |
| `Fasteners ▸ 1 / 2 / 3` | Crank-bolt extraction. Out of scope this pass, and it restructures the oil pan. |
| `Phase 1 ▸ 3 / 4 / 5`, `Test Fixtures ▸ *` | Superseded by the Assembly and Piston tools. |
| `Alignment ▸ *` | Already applied. Only re-run if the anchor scale regresses. |

Yes — **`UI ▸ 4` supersedes `UI ▸ 3`**, and the Fastener and `Interaction ▸ 2` tools remain
skipped.

### [QV] Quest checks

1. Pistons grabbable after `Grab ▸ 2` — the nested-Rigidbody fix, still unproven on device.
2. Piston **components** individually grabbable at their real size; they are small, and hand
   tracking may struggle. This is the likeliest failure in this pass.
3. Whether the bench build zone is reachable and clear of the engine.
4. Panels stable, no jitter, rays reach the buttons from the seated pose.
5. Ghost reads as a transparent target, not a solid part.
6. Arrow orientation correct (`meshForward` may need one change once the prefab axis is seen).
7. Panels fade rather than pop on support change, with no stale text.

### [PC] Physical calibration

- Component tolerances: 25 mm / 25° for piston parts, 40 mm / 25° for installs. Provisional.
- Build-zone position and per-kit spacing (0.22 m apart).
- Tray staging grid spacing (45 mm).
- Ghost alpha 0.25; panel `Reduced` 0.55 and `Faded` 0.18 alpha.
- Rig height 0.20 m above and 0.10 m beyond the marker.

### Genuine blockers, unchanged

- **No tool model.** `ToolInteraction` has four switchable detection rules and is ready for a
  prefab.
- **Crank bolts and caps are baked into the oil pan.** Extraction exists but is out of scope
  here.
- **No camshaft-specific holders in this model**, so the camshaft stage stays
  place-and-validate.

Nothing in this pass has run on a headset. Both assemblies compile with zero errors; that is
not validation.

---

## Claude → Reviewer — 2026-10-06 — Interaction layer freeze: surface placement, lifecycle, snap assembly, physics

Your manual scene is the base. It was committed untouched first (`2bd1a17`), so every
line of this pass is diffable against it. Nothing in this pass runs an Editor builder.

### What I did NOT touch (verified against the checkpoint)

| Item | Diff vs `2bd1a17` |
|---|---|
| Panel positions, widths, text/layout | none |
| `GhostTarget.mat` (green) | none |
| `arrow.prefab` rotation | none |
| Piston components and their grab config | none |
| `1 - ArUcoMarkerTracking.unity` | **one line**: the serialized `Demo:` caption blanked |

### 1. Surface placement replaces the ArUco startup

**How the workspace transform is established.** A new `WorkspacePlacement`
(`Assets/Scripts/MR/`) is created at runtime by `StepManager.Awake` when
`useSurfacePlacement` is on (default). It writes the **same transform ArUco wrote**:
`MarkerAnchor`, the top of the anchor chain. Everything below it — `CalibrationOffset`
(grounding lift), `EngineAnchor`, trays, ghosts, `TabletopSupport`, `PanelRig` — keeps
its marker-relative geometry, so downstream code did not change.

Pose convention is the marker's: MarkerAnchor **+Z = surface normal (up)**, +Y points
away from the viewer, `yawOffsetDegrees` about the normal, plus `surfaceUpOffset`
(1 cm) above the hit. `CalibrationOffset` still lifts the model by its grounded
0.224 m, so the oil pan sits on the desk and the pad collider lies on it.

**Surface source.** Meta's `EnvironmentRaycastManager` (MR Utility Kit, Depth API,
no room setup). `EnvironmentDepthManager` is created with occlusion shaders OFF and
runs only while placing. If depth is unsupported or silent for 4 s, a horizontal
plane at `fallbackTableHeight` (0.72 m above floor) is used and the screen says so.
The Editor always uses the plane, so the flow is play-mode testable.

**Pointing ray**: tracked controller → hand pointer pose → head gaze.
**Confirm**: the `Place Workspace` button (ray-clickable), index trigger, or index
pinch. **Reposition**: right thumbstick click; the engine follows the reticle until
confirmed again. Panels do not move on reposition — they are already detached.

**Flow**: passthrough → placement screen on the Home panel (same panel, same button)
→ reticle → confirm → `StepManager.AnchorLocked` → PanelRig locks → onboarding →
Begin → sequence. Logged as `workspace_placed` (provider, position, normal, confidence,
reposition flag).

**What happens to ArUco at runtime.** With `useSurfacePlacement` on: the
`[ApplicationCoordinator]` is disabled before its `Start()`, the CV debug quad is
deactivated, and the `Passthrough Camera Access` object is switched off, so no camera
permission or CPU goes to it. The code, the scene objects and the marker path are
intact; set `useSurfacePlacement` off to get the original startup back. `StepManager`
never waits for marker 0 any more.

### 2. The `Demo:` text — exact source

`DemoUICanvas/Panel/CaptionText` (TextMeshProUGUI, fileID 1676478110) carried the
serialized string `Demo: Insert the crankshaft (click Got It when ready)` from the
original prototype. It is `StepPresenter.captionText`, the single-field fallback.
Because `titleText` is also assigned, the presenter took the preferred path and
**never wrote or cleared the fallback**, so the serialized string stayed on screen
for the whole session. Two fixes: the string is blanked in the scene (the one-line
scene change), and `StepPresenter.ClearText` now always clears the fallback even when
it is not the active layout. `Step_0_Demo.asset`, `DemoScene.unity`, `main.unity` and
`DemoUICanvas.prefab` still contain the text; none is in the build set or the runner.

### 3. One presentation lifecycle

Three components used to write the same TextMeshPro fields (StepPresenter,
ParticipantCard, AssemblySessionController). Now:

- **ParticipantCard** is the only writer of card text. `StepPresenter` detects it and
  only clears the legacy fields. The session controller's completion caption is
  skipped when a card exists.
- **Action guidance was never shown.** `AssemblyAction.ghostKeys` had no consumer:
  the piston component targets (`ghost.PistonKit001.PistonHead` …) never appeared.
  `StepPresenter.PresentAction()` is now called from `EnterAction`: it deactivates
  the previous action's ghosts, then shows this action's (falling back to the
  stage's level block when the action has none). Whether a level shows ghosts at all
  still comes from the authored level block, not code.
- Every transition clears first: ghosts, spawned prefabs, correction feedback
  (success feedback finishes its 1.2 s hold), AlignmentChip baseline, status line.
- Ghosts are made **inert** when shown: kinematic, no gravity, colliders and grab
  off. The kit ghosts carried a Rigidbody and `releaseToGravityOnEnable`, so they
  would have fallen the moment they appeared.
- A support-level change re-presents the same stage **and the same action**; it
  touches no workflow state.
- Sequence completion clears all guidance.

### 4. Registry binds to your hierarchy by convention

The `part.PistonKit00N.*` entries pointed at deleted objects (`fileID 0`).
`GuidanceRegistry` now resolves unbound keys against the live scene and logs each
binding once:

```
part.PistonKit001.ConnectingRod -> Components/PistonKits/piston001/ConnectingRod
part.PistonKit001               -> .../piston001/PistonHead   (the kit handle)
ghost.piston001.PistonHead      -> Ghosties/piston001/PistonHead
```

The four step assets' `install` actions now target `ghost.piston00N.PistonHead`
(the head's pose) instead of the ghost group pivot. `ghostKeys` still show the whole
ghost piston.

### 5. Piston mating — how collisions are suppressed and restored

In `StepValidator`:

- **Grab state is read from the Interaction SDK** (`PointableElement.SelectingPointsCount`)
  instead of inferred from `isKinematic`. The old inference read a kinematic-by-design
  part as "held forever" and never judged it.
- Within `assistRadiusMeters` (8 cm, or 1.5× tolerance) of the target:
  `Physics.IgnoreCollision` is set between the moving part (and anything joined to it)
  and the **receiving set** — every non-trigger collider under the workspace root
  except the part itself and bench furniture (`tray*`, `Plane`, `TabletopSupport`).
  Mating geometry may overlap; the table stays solid.
- **Release inside the radius** freezes the part where the hand let go and judges
  it after 0.12 s. Valid → snap, lock, join, assist stays off for that part.
  Invalid → kinematic state restored, collisions restored (the engine pushes the
  overlap apart at 1 m/s, not the project's 10 m/s), feedback shown, next attempt.
- **Leaving the radius** restores collisions. Away from the target everything is as
  before: free, solid, under gravity.
- **Kits**: a locked `part.PistonKit001.X` is parented under the kit handle
  (`PistonHead`). When the `install` action begins, the handle's lock is lifted so it
  can be grabbed; the joined components ride with it as transform children and stay
  locked. Install is judged head-vs-ghost-head.

Sequence per piston: head → rod → pin → rod end → retaining bolt → install, with the
other three fasteners authored but disabled as before.

### 6. Slow-motion physics — what caused it

Project time and gravity are normal (`fixedDeltaTime 0.02`, `timeScale 1`, gravity
−9.81, drag 0, mass 1, scale of the parts is irrelevant to fall speed). No script
sets any of them. The cause is in `DropIntoTray`, which overrides every part's
Rigidbody in `Awake` regardless of the Inspector values you see:

1. **`ContinuousSpeculative` CCD** on every part. Speculative contacts are created
   ahead of motion, so a small part decelerates *before* it touches anything — the
   classic floaty, hovering look.
2. **Project contact offset 1 cm** (`DynamicsManager.m_DefaultContactOffset`) — a
   1 cm cushion on a 2 cm pin, so parts rest and interact visibly above surfaces.
3. **`RigidbodyInterpolation.Interpolate`** forced on. A kinematic body moved by the
   hand through its Transform renders a physics step behind the hand, so carried
   parts lag and smear.

Fix (no scene edit, no grab-config change): a new `physicsProfile` field, default
`Tuned`, which applies to every existing part because the field is new: sweep-based
`ContinuousDynamic` (tunnelling still prevented), interpolation `None`, contact offset
3 mm on the part's own colliders, depenetration 1 m/s. `PerObject` restores the two
authored fields on that object. This is a diagnosis from inspection; it is **[QV]**.

### 7. Progression

Unchanged in principle, confirmed in code: progress derives from validated actions in
`WorkflowState`, increments once (idempotent set), participant Continue is gated by
`CanAdvance`, a support change cannot advance/reset/unlock/duplicate.

### Files changed

```
new   Assets/Scripts/MR/WorkspacePlacement.cs
mod   Assets/Scripts/Steps/StepManager.cs            provider swap, ArUco bypass
mod   Assets/Scripts/Steps/StepValidator.cs          SDK grab state, snap assist, kit join
mod   Assets/Scripts/Steps/StepPresenter.cs          action ghosts, text ownership, inert ghosts
mod   Assets/Scripts/Steps/GuidanceRegistry.cs       convention binding
mod   Assets/Scripts/Steps/AssemblySessionController.cs  PresentAction hook, grab/lock/placement logs
mod   Assets/Scripts/UI/OnboardingSequence.cs        placement screen first
mod   Assets/Scripts/UI/ParticipantCard.cs           feedback cleared per action
mod   Assets/Scripts/UI/AlignmentChip.cs             per-action tolerance and reset
mod   Assets/Scripts/UI/ResearcherHud.cs             WORKSPACE line, assist flag
mod   Assets/Scripts/DropIntoTray.cs                 Tuned physics profile
mod   Assets/Scripts/Logging/SessionLogger.cs        workspace_placed event
del   Assets/Scripts/Steps/PistonAssembly.cs         superseded, was never in the scene
mod   Assets/Editor/PistonAssemblySetup.cs           OBSOLETE menu names + confirm dialog
mod   Assets/Editor/AdaptiveUiSetup.cs               confirm dialog: overwrites tuned layout
mod   Assets/ScriptableObjects/Steps/Step_02..05     install targetKey -> ghost.piston00N.PistonHead
mod   Assets/1 - ArUcoMarkerTracking.unity           one line: Demo caption blanked
mod   CLAUDE.md                                      startup + render pipeline corrected
```

### Editor commands to run: **none**

Open the scene, press Play or build. Unity will generate
`WorkspacePlacement.cs.meta`; commit it. Do **not** run `UI ▸ 4`, any `Piston ▸ *`,
`Interaction ▸ 2`, `Fasteners ▸ *` or `UI ▸ OBSOLETE 3`; the two that could damage the
tuned scene now ask for confirmation.

Optional, Inspector only: add a `WorkspacePlacement` component to `StepManager` to
expose its tunables (`surfaceUpOffset`, `yawOffsetDegrees`, `fallbackTableHeight`,
reposition button). Without it the runtime-created one uses the defaults above.

### Quest acceptance test — in this order, all [QV]

1. Launch: passthrough, no marker prompt, no camera-permission dialog. The Home panel
   reads "SET UP / Place the workspace". **[QV]**
2. Point at the desk with the controller (or hand). **[QV]**
3. A cyan ring with a centre dot sits on the desk and follows the ray smoothly; amber
   on a wall. **[QV]** If the body text says "estimated table height", depth was
   unavailable — note it.
4. Press `Place Workspace` (or trigger / pinch). The ring disappears; "Workspace
   placed." **[QV]**
5. Engine and tray parts appear on the desk, not inside it, and stay put. **[QV]**
   If the engine is rotated relative to you, set `yawOffsetDegrees`. **[PC]**
6. Panels sit where you tuned them (20 cm above the placed workspace) and do not move
   afterwards. **[QV]**
7. No `Demo:` text anywhere, through all stages. **[QV]**
8. Crankshaft: grab, place on the green ghost, release → "Placed correctly", lock,
   progress 1 action, next instruction, old ghost gone. **[QV]**
9. Piston head grabs. **[QV]**
10. Bring the rod to the head ghost: within ~8 cm it passes into the head instead of
    bouncing off. **[QV]** The build zone is `Ghosties/PistonKits/PistonKit00N`
    (16 cm above the desk, 58 cm along +Z); move those four roots if it is not
    reachable. **[PC]**
11. Release the rod in place: snaps, locks, joins the head. Release it badly: it is
    pushed out gently and the card says why. **[QV]**
12. Pin inserts and snaps the same way. **[QV]**
13. Drop a free part from 10 cm: it falls at normal speed and rests on the tray, not
    1 cm above it; carried parts do not trail the hand. **[QV]**
14. Between actions: instruction, feedback, ghost and arrow all change together, with
    nothing left from the previous action. **[QV]**
15. Progress changes exactly once per completed required action; B/Y support changes
    change nothing but panels and detail. **[QV]**
16. Right thumbstick click: engine follows the ring; confirm; panels unchanged. **[QV]**

### Physical calibration **[PC]**

- `surfaceUpOffset` 1 cm, `yawOffsetDegrees` 0, `fallbackTableHeight` 0.72 m.
- `assistRadiusMeters` 8 cm, `nearReleaseSettleSeconds` 0.12 s.
- Contact offset 3 mm, depenetration 1 m/s.
- Position of the four ghost kit build zones.

### Not done, by instruction

AI, OpenAI, HRV/BLE, simulation, WebXR, visual redesign, tool model. The crank bolts
remain baked into the oil pan. Nothing here has run on a headset.

---

## Claude → Reviewer — 2026-10-06 (b) — Quest polish pass: startup panel, single UI path, arrow, trays, feedback, interchangeable pistons, work surface, speech, haptics

Base: `c18bad2` (your Quest-tested build). Architecture unchanged; scene hierarchy,
tuned panel dimensions, green ghosts, grab configuration and the placement flow are
as you left them. Scene diff: Unity's own re-serialisation of new fields plus **one
line** (AlignmentChip moved up). `GhostTarget.mat`, `arrow.prefab` and the ISDK prefab
are untouched. Your open Unity had further uncommitted edits (EngineAnchor rotated
90° about Z, StartButton and ResearchCanvas sizes, two anchored x offsets); they are
committed exactly as found.

### 1. Startup panel — exact cause and fix

`PanelRig` is a **child of `MarkerAnchor`**, and during placement the previous pass
moved `MarkerAnchor` every frame to the candidate/idle pose. A child inherits every
move of its parent, so the whole rig — all four canvases — travelled with the head.

Now (`PanelRig`, `AdaptivePanelController`, `WorkspacePlacement`):
- At start the rig **detaches** from the anchor chain and parks the main panel
  0.75 m in front of the head, 8 cm below eye height, facing the viewer. It is
  **world-locked**: nothing moves it until placement. (Left thumbstick re-parks it.)
- `AdaptivePanelController.SetPlacementMode(true)` hides Steps / Performance /
  Research; only the main panel exists during placement and the walkthrough.
- The workspace root no longer moves before the first placement; only the reticle
  does. (On a *reposition*, the engine still previews under the ring — the rig is
  already detached by then.)
- On `Place Workspace`: the rig settles **once** relative to the placed workspace with
  the same offsets as before and locks for the session. The side panels are revealed
  when the participant presses **Begin Assembly** (so the walkthrough stays one
  panel); their positions are your tuned layout, relative to the placed workspace.
  Nothing follows the head afterwards.

### 2. Duplicated Step 1 UI — exact cause

Not two canvases: **two layouts inside one panel**. `DemoUICanvas/Panel` still carries
the fields of the earlier presenter layout *and* the card layout, all active:

| Earlier layout (StepPresenter era) | Card layout (ParticipantCard) |
|---|---|
| `Eyebrow` "V8 ASSEMBLY" (y −24) | `Header` "V8 ASSEMBLY" (y −22) |
| `StepLabel` "STEP 01 / 06" (written by StepPresenter) | `ProgressLabel` "STAGE 1 / 6 CRANKSHAFT STEP 1 / 3" |
| `CaptionText` (the old `Demo:` field, blanked last pass) | `TitleText` (instruction) |
| `StatusLine` | `FeedbackText` |
| `NextButton/Text (TMP)` "Next Step" | `NextButton/Label` "Continue" |

So the header, the progression text and the Continue caption each rendered twice,
one on top of the other. `ParticipantCard.Awake` now **switches the earlier layout's
objects off** (`Eyebrow`, `StepLabel`, `CaptionText`, `StatusLine`, and every TMP inside
the Continue button except its own label) — removed from the runtime path, not
blanked. `StepPresenter` no longer writes the step label when a card owns the text.
At runtime there is one instruction field, one progress line, one Continue label,
one feedback field.

**The "Demo step".** There is no Demo step in the workflow: `StepRunner.steps` holds
exactly `Step_01_Crankshaft … Step_06_Camshaft` (verified by GUID). `Step_0_Demo`,
`Step_1_Crankshaft` and `Step_Crankshaft_Phase1` are orphaned assets referenced only by
the two dead scenes; nothing loads them. What read as a demo layer was the earlier
layout above — it is gone. Flow is: placement → walkthrough → Step 1.

### 3. Guidance arrow

The prefab mesh is a flat 2-D arrow (`arrow.fbx`, geometries Plane.005/006: long axis X,
thin axis Z, tip at the narrow end). Your prefab root rotation `(0.5, −0.5, −0.5, 0.5)`
maps the mesh's long axis onto world ±Y — i.e. you authored it tip-down. The runtime
ignored that: it overwrote the root rotation with its own computed `MinusY` aim, which
is why it lay roughly parallel to the target.

`GuidanceArrow` now has `orientation = AuthoredTipDown` (default): it **keeps the
prefab's authored rotation** (tip straight down, 18 cm above part or target) and only
**yaws it about the vertical** so the flat face is towards the head (`faceViewer`). The
thin axis is read from the mesh bounds, not guessed. The old computed mode remains
selectable.

### 4. Trays

Each tray is a thin non-convex mesh shell scaled ~60×. A thin shell has no "inside":
a part released inside a wall is pushed out on whichever side is nearer, one authored
overlapping the floor can be pushed down through it, and a held (kinematic) part
passes through anything. `TrayColliders.Ensure` adds, at startup, **five thick box
colliders** from the mesh bounds to each `tray*`: a 5 cm slab under the floor and
four 2 cm walls extending 3 cm above the rim. Boxes have an unambiguous inside, so
"out" is always up and over the rim. Sweep CCD from last pass stays on.

The mating assist never touched the trays: its receiving set excludes any collider
under `tray*`, `Plane`, `TabletopSupport`, and anything carrying `AssemblyWorkSurface` (checked by component, because the prefix list is already serialized in the scene). Confirmed in code.

### 5. Alignment chip vs Back button

Both were bottom-left anchored at (22–24, 22–24) px with the same size — the chip sat
exactly on the button. The chip's `anchoredPosition.y` is now 90 (button 56 px + gap).
That is the single scene line.

### 6. Wrong-item feedback — lifecycle

Previously "wrong component" was only a *reject reason after a release far from the
target*, and it stayed while the action was blocked. Now the validator **watches every
other registered part** (`OnWrongPartGrabbed` / `OnWrongPartReleased`) and the card
tracks current state:

- wrong part grabbed → amber "That is not the required component" + warning haptic +
  spoken phrase, logged as `wrong_component_grabbed` (part key + requested role);
- wrong part released → cleared;
- an **eligible** part grabbed → any correction cleared immediately;
- valid placement → "Placed correctly" (1.2 s), "Correct.", success haptic;
- action change → everything cleared; a wrong part still in the hand re-raises.

### 7. Interchangeable piston parts

Validation is by **role**. `GuidanceRegistry.RoleCandidates("part.PistonKit001.PistonHead")`
returns every registered `part.PistonKit00k.PistonHead` (k = 1..4); the validator accepts
any of them that is not **consumed** (`_consumed` is keyed by *instance*). Rods, pins,
ends and the enabled bolt work the same way. `interchangeableRoles` can turn it off.

**Binding.** The instance that locks for the handle role (`PistonHead`) is bound to the
kit the *action* names (`GuidanceRegistry.BindKitHandle("PistonKit001", head003)`); from
then on `part.PistonKit001` resolves to that head, the rod/pin/end/bolt of that stage
are parented under it when they lock, and `install` moves it. A consumed instance can
never satisfy a later kit. Logging: `component_locked` now records `part_key` =
**instance** (e.g. `part.PistonKit003.PistonHead`), `requested_key` = the role asked for,
and `instance_name`; `component_grabbed/released` already carry the instance.

### 8. Work surface

`AssemblyWorkSurface` (runtime, created by `StepManager` after placement, re-posed on
reposition): a 32 cm square, **10 cm above the detected desk**, placed toward the
participant from the engine **block** (`Ghosties/oilPan` bounds + 10 cm clearance +
half size), falling back to user-right / user-left / further out if the first spot
overlaps the block, the trays or the parts in plan view. Faded cyan grid on a
`Sprites/Default` quad (always-included shader, alpha-blended, edges fade — not a
slab), plus a 2 cm `BoxCollider` slab so dropped parts rest on it. It is in the
validator's always-solid list.

**Piston sequence.** All four ghost kits (`Ghosties/PistonKits/PistonKit00N`) are posed
on the surface at the same spot: their children are set to the **assembled relative
poses copied from the bore ghost `Ghosties/piston00N`**, the kit is turned so the rod
hangs below the head and the pin axis runs across the view, and lifted so the lowest
point clears the surface by 1.5 cm. So: head target on the surface → head snaps and
locks → rod ghost relative to that head → pin → end → bolt → the finished unit is
unlocked and installed in the bore (`ghost.piston00N.PistonHead`), next piston reuses
the surface. The previous "build zone" ghosts were the *staged* (spread-out) copies,
58 cm behind the engine — that is why the head ghost was far and uncontextualised.

### 9. Why rod/pin still repelled

The assist radius was measured **pivot to pivot** (8 cm). A rod is ~12 cm long: its
end touched the locked head while the pivots were still >8 cm apart, so collisions
were still on at first contact and the release outside the radius went through the
unfrozen path (dynamic, overlapping, pushed apart). The assist is now **shape-to-shape**:
pivot distance minus both bounding-sphere radii, threshold `assistGapMeters` (6 cm).
The receiving set already included the whole growing assembly (locked parts under the
workspace root); environment, trays, work surface stay solid.

### 10. Piston text truncation — cause

`TitleText` is a **42 px tall** field with `overflowMode = Truncate` and auto-size
22–30. One line of 22 pt is ~26 px, so any instruction that needs a second line at
416 px width — every piston instruction — was cut after the first line. Not the
strings (all complete in the assets), not another writer. `ParticipantCard.
EnsureInstructionFits` sets overflow to Overflow, wrapping on, min size 18, height to
two lines (49 px), and moves only the detail and feedback fields down by the 7 px
difference. Panel width and the rest of the layout are untouched. Piston instructions
were reworded for the work surface (and so their clips match):

```
Find the piston components in the parts tray.
Place a piston head on the work surface target.
Fit a connecting rod into the piston head.
Push a connecting pin through the rod and the head.
Fit a rod end cap onto the connecting rod.
Fit a retaining bolt.
Install the assembled piston into its cylinder bore.
```

### 11. Audio

There is no TTS engine in the project or on Horizon OS; the Step 1 clip
(`InsertCrankshaft.mp3`, authored only on the L3 block) was a pre-rendered file. The
same mechanism — pre-rendered clips through the scene `AudioSource` — now covers every
enabled action: 18 WAVs synthesised offline (Windows SAPI, Zira, fixed rate, mono
22 kHz) into `Assets/Resources/AdaptiveAR/Speech/`, **named by a key derived from the
exact sentence**. `SpeechLibrary.KeyFor(text)` and the generator use the same rule, so
the clip is looked up with the string the card shows; a wording change without a
clip logs the missing key once. Rules in `InstructionSpeech` / `StepPresenter`:
spoken **once on action enter**, never per frame, never on a support-level re-render;
a stage's authored clip plays only if its first action has no sentence clip.
Feedback: "Correct." on a valid placement, "That is not the required component." on
a wrong grab, layered so they never cut an instruction.

### 12. Haptics

`HapticFeedback` (static, `OVRInput.SetControllerVibration` on the controller nearest
the part, auto-stopped): grab 60 ms @0.25, wrong part 2×70 ms @0.6, success 140 ms
@0.9. Every call is try/caught; no controller → nothing.

### Files changed

```
new   Assets/Scripts/MR/AssemblyWorkSurface.cs
new   Assets/Scripts/MR/TrayColliders.cs
new   Assets/Scripts/Audio/SpeechLibrary.cs          SpeechLibrary + InstructionSpeech
new   Assets/Scripts/XR/HapticFeedback.cs
new   Assets/Resources/AdaptiveAR/Speech/*.wav (+.meta)  18 clips
mod   Assets/Scripts/UI/PanelRig.cs                  world-locked pre-placement park
mod   Assets/Scripts/UI/AdaptivePanelController.cs   SetPlacementMode
mod   Assets/Scripts/UI/GuidanceArrow.cs             AuthoredTipDown + face viewer
mod   Assets/Scripts/UI/ParticipantCard.cs           single layout, feedback lifecycle, text fit
mod   Assets/Scripts/Steps/StepValidator.cs          roles, wrong-part monitor, shape-gap assist, binding
mod   Assets/Scripts/Steps/GuidanceRegistry.cs       RoleCandidates, kit bindings, PartKeys
mod   Assets/Scripts/Steps/StepPresenter.cs          speech on action enter
mod   Assets/Scripts/Steps/AssemblySessionController.cs  speech/haptic/log hooks, instance logging
mod   Assets/Scripts/Steps/StepManager.cs            work surface, tray colliders, panel placement mode
mod   Assets/Scripts/MR/WorkspacePlacement.cs        root no longer moves before first placement
mod   Assets/Scripts/Logging/SessionLogger.cs        wrong_component_grabbed; locked instance fields
mod   Assets/ScriptableObjects/Steps/Step_02..05     instruction wording
mod   Assets/1 - ArUcoMarkerTracking.unity           AlignmentChip y 24 -> 90 (+ Unity field re-serialisation)
```

### Editor commands: **none.** Unity already generated the new `.meta` files; they are committed.

### [QV]

1. Startup: one world-locked panel in front of you; it does not move with the head.
2. After `Place Workspace`: panel stays; after `Begin Assembly`: your three-panel layout
   appears relative to the workspace and stays.
3. Step 1: one header, one progress line, one instruction, one Continue, no `Demo:`.
4. Arrow: tip down, above the part/target, face towards you.
5. Drop a part into a tray from 15 cm: it stays in. Push one at a wall: it stays in.
6. "Alignment needed" chip clear of Back.
7. Grab the camshaft during Step 1: amber text + warning buzz + spoken phrase; release:
   cleared; grab the crankshaft: cleared.
8. Pistons: pick **any** head; it binds; rod/pin/end/bolt follow it; install.
9. Work surface: 10 cm above the desk, toward you, beside the block, not overlapping
   it — if it is in the wrong spot, `clearanceFromEngine` / `heightAboveDesk` **[PC]**.
10. Rod enters the head without repulsion; pin likewise.
11. Piston instructions complete, two lines.
12. Each new action speaks once; B/Y re-render speaks nothing.
13. Grab / wrong / success pulses on the hand that touched the part.

Nothing in this pass has run on a headset.

---

## Claude → Reviewer — 2026-10-07 — Calibration/interaction cleanup: yaw, panel centring, work-surface lifecycle, role tolerances, immediate snap, READ/INTERACT, audio root cause, error rules

Base: `1eeeaf1`. No scene edit by me this pass; the scene diff is your own uncommitted
Editor work (StatusCanvas moved/rotated, a `Worksurface` object carrying
`AssemblyWorkSurface` under `Offset`, build profile target), committed as found. Side
panels are untouched: the rig moves as one unit.

### 1. Workspace yaw

`WorkspacePlacement.ApplyPose` now takes the **head's forward projected onto the
horizontal plane at confirmation** as the viewing axis (previously the head-to-hit
direction, so a glance sideways skewed the engine). MarkerAnchor: +Z = world up,
+Y = that axis, `yawOffsetDegrees` about up. Pitch/roll never reach the engine. Your
EngineAnchor 90° Z correction is kept; it squares the oil pan to the axis.

### 2. Main panel centred without touching the side panels

Cause of "panel left of me": the rig origin was placed on the axis, but the main
canvas sits at **(−0.30, +0.20) inside the rig**, so the panel landed 30 cm left.
`PanelRig.ComputeTarget` now solves for the rig pose that puts the **main panel's
centre** at `engineBlockCentre + up·heightAboveMarker + viewAxis·depthBeyondMarker`,
facing back along the axis (`position = panelCentre − rotation · mainPanelOffset`).
The side canvases are children with your tuned offsets; they move with the rig and
are never written. The view axis is frozen from MarkerAnchor at placement.

### 3. Work surface lifecycle and location

- Created/shown by `StepManager.HandleStageChanged` only when the entering stage
  has an enabled action with a kit part key (stages 2–5); hidden when a non-piston
  stage enters (camshaft) — so it does not exist during the crankshaft.
- `AssemblyWorkSurface.Place` mode `AboveEngine` (default): 10 cm above the **top of
  the engine block**, over its centre, shifted 8 cm towards the participant, yawed to
  face them. Your authored `Worksurface` object is used as the component host and
  follows the engine (it is under `Offset`); set `placementMode = Authored` on it if
  you prefer its own transform. Ghost kits are re-arranged on it each time it shows.

### 4. Tolerances per role (new `StepValidator.roleTolerances`; action values are the fallback)

| Role | Position | Orientation | Symmetry |
|---|---|---|---|
| PistonHead | 3.0 cm | 30° | full |
| ConnectingRod | 3.5 cm | 30° | long axis + roll mod 180° |
| ConnectingPin | 3.0 cm | 40° | axis ± (either way), roll ignored |
| PistonEnd | 3.0 cm | 35° | long axis + roll mod 180° |
| pistonBolt / PistonNut (and *Other) | 3.0 cm | 45° | axis ±, roll ignored |
| crankshaft / camshaft | 4.0 cm | 25° | long axis, roll ignored (they turn in their bearings) |
| install (kit handle) | action: 4.0 cm | 25° | full |

Attempt zone = max(10 cm, 3 × position tolerance).

### 5. Symmetry-aware orientation

Each part's mechanical axis is its longest mesh extent in local space (pin: local Z).
`AxialFree`: angle between part axis and target axis. `AxialFreeFlip`: the same with
`min(a, 180−a)`. `AxialHalfTurn`: that axis angle plus the residual roll about the
axis after the axes are aligned, taken `min(r, 180−r)`; the error is the larger of
the two. `None`: `Quaternion.Angle`. The HUD shows the rule in force.

### 6. Immediate snap

The validator judges every frame; when position AND orientation are inside tolerance
— in the hand or not — `Succeed()` runs **once** (`_completed` guard; `IsActive` false
afterwards): exact pose → `PlacementLock.LockAt` (kinematic, SDK components disabled,
which ends the grab) → join to the kit handle → success event → `component_locked` →
workflow advances once. Because the SDK restores the pre-grab Rigidbody state and
applies a throw velocity *after* the lock ran, `PlacementLock` re-asserts pose and
kinematic state for 6 frames. No release is required. **[QV]** the "BOOM" feel.

### 7. READ vs INTERACT

`AssemblySessionController.IsReadState` = current action is an acknowledgement.
- READ: `AttentionFader` dims every loose part to 45 % via `MaterialPropertyBlock`
  (`_Color`/`_BaseColor`; materials untouched) and disables their Interaction SDK
  components (never on locked parts); `StepPresenter` shows no ghost; `GuidanceArrow`
  hides; the card shows **Continue →**.
- INTERACT: blocks cleared, components re-enabled, ghost and arrow up, Continue
  hidden; validation completes the action. If a physical action cannot be armed
  (missing object) Continue reappears so nobody is stuck.
- Fading touches no identity, registry, physics, validation or progress; the only log
  effect is the optional `instruction_acknowledged` note.

### 8. Continue / Back

Continue: 236×66, bold 22 pt "Continue →", accent fill when active, hidden in
INTERACT. The scene's own `AdvanceStepManually` wiring still advances; the card only
logs `instruction_acknowledged`. **Back** (previously wired to nothing): "← Back",
quiet fill, shown only when `CanGoBack` — i.e. from a physical action whose preceding
enabled action in the stage is an instruction. It re-enters that instruction
(validator cleared, ghosts off); completed-action state is never modified, nothing
physical is undone. Otherwise hidden.

### 9. One validation cue

`AlignmentChip` is switched off at start. The feedback line in the main panel is the
only cue, from `StepValidator.CurrentCue`:
outside the attempt zone → nothing (the instruction stands); in zone, position off →
"Move closer to the highlighted position"; position good, orientation off → "Turn the
part to match the ghost"; released in zone without success → "Almost there — align
with the ghost" (until the next grab); wrong part held → "That is not the required
component"; success → "Correct" (1.2 s). No numbers.

### 10. Audio — root cause and fix

Two causes, both in `SpeechLibrary.cs`:

1. **Unloaded clips.** The WAVs import with `Preload Audio Data` off (inherited from
   the project's mp3 importer). Such a clip has no audio data until
   `AudioClip.LoadAudioData()` runs; `PlayOneShot` on it is silent and `Play()` is
   not reliably in time. The crankshaft stage was the one place a clip had been
   touched early enough / the authored mp3 path existed, which is why only it was
   heard. Now: `SpeechLibrary.Preload()` loads all clips at startup
   (`Resources.LoadAll` + `LoadAudioData`), and every play waits for
   `loadState == Loaded`.
2. **"Correct." was cut.** Instructions called `Stop()` on the single scene
   `AudioSource` before playing; `Stop()` also kills one-shots on that source, and
   the next instruction always followed a success in the same frame. Now two
   dedicated 2-D sources: instruction (replaced) and feedback (never cut); an
   instruction requested while feedback plays waits for it (≤ 2 s).

Logging on every request: `[Speech] instruction request "…" -> key '…' -> found,
loadState=…` then `Play '…' on …/instruction, isPlaying=…`, and
`[Speech] Preload: N clip(s)` at start — **N = 0 on device means Resources are not in
the build.** The authored mp3 path goes through the same `PlayAuthored`. Repeat guard
1 s; nothing per frame; nothing on support re-render. **[QV]** against logcat.

### 11. Error counting and JSONL

**Increments the error count:** `wrong_component_grabbed` (one per grab transition of
an ineligible part during an INTERACT action; `error_type: wrong_component`,
`part_key` instance, `requested_key` role) and `validation_attempt` with
`success:false` (`error_type: incorrect_position | incorrect_orientation`).

**Does not:** `component_dropped` (correct part released outside the attempt zone),
`component_grabbed/released`, `instruction_acknowledged`, `back_to_instruction`,
advance_blocked notes.

**Attempt definition:** begins when the handled correct part enters the attempt zone;
ends with `validation_attempt success:true error_type:placement_success`
(trigger `aligned_in_hand` / `aligned`) or exactly one failure on `left_zone` or
`released_in_zone`. Classification uses the closest approach: never inside position
tolerance → `incorrect_position`; position reached, orientation never → `incorrect_orientation`.
**Dedup:** one event per zone entry; frames inside the zone update the best values
only; a new attempt requires leaving and re-entering, or grabbing the part again.
Wrong-component and failed placements are distinct events with distinct
`error_type`s. `component_locked` still records the instance (`part_key`),
`requested_key` and `instance_name`.

### 12. Files changed

```
new   Assets/Scripts/Steps/AttentionFader.cs
mod   Assets/Scripts/Audio/SpeechLibrary.cs         preload, load-state wait, two sources, logging
mod   Assets/Scripts/Steps/StepValidator.cs         role tolerances, symmetry, immediate snap, attempts, cues, drops
mod   Assets/Scripts/Steps/PlacementLock.cs         re-assert after lock-while-held
mod   Assets/Scripts/Steps/AssemblySessionController.cs  READ state, Back, acknowledgement, error rules, unarmed guard
mod   Assets/Scripts/Steps/StepPresenter.cs         READ: no ghost, attention fader
mod   Assets/Scripts/Steps/StepManager.cs           work surface per piston stage
mod   Assets/Scripts/MR/AssemblyWorkSurface.cs      AboveEngine / Authored, Show/Hide
mod   Assets/Scripts/MR/WorkspacePlacement.cs       yaw from head forward
mod   Assets/Scripts/UI/PanelRig.cs                 main panel on the view axis
mod   Assets/Scripts/UI/ParticipantCard.cs          states, controls, single cue
mod   Assets/Scripts/UI/GuidanceArrow.cs            hidden while reading
mod   Assets/Scripts/UI/ResearcherHud.cs            effective tolerance + symmetry
mod   Assets/Scripts/Logging/SessionLogger.cs       component_dropped, error_type
```

Editor commands: **none**. Unity will generate `AttentionFader.cs.meta`.

### 13. [QV]

1. Engine square to your facing direction after Place; no tilt.
2. Main panel above and beyond the oil pan on your axis; side panels at your offsets.
3. No work surface during the crankshaft; appears above the block at the first piston
   stage; gone at the camshaft.
4. Pin/bolt accepted rolled or flipped; rod accepted flipped about its axis.
5. Aligning in the hand snaps instantly, once; the hand lets go cleanly; nothing falls.
6. READ: parts dimmed, un-grabbable, no ghost/arrow, Continue →. INTERACT: all back,
   no Continue. Back only on a physical action, returns to its instruction.
7. One feedback line; chip gone.
8. Logcat shows `[Speech] Preload: 18 clip(s)`; each action speaks once; "Correct."
   audible after a snap.
9. Dropping the right part on the desk logs `component_dropped`, no error.

Nothing in this pass has run on a headset.

---

## Claude → Reviewer — 2026-10-10 — Final interaction/content pass: station height, pinned side panels, READ state, tolerances v2, assembly mat sequence, L1/L2/L3 pistons, component preview, audio root cause, spatial feedback, error schema

Base: `02ac59f`. Architecture, placement, green ghosts, haptics, logging and your
side-panel offsets are untouched. Scene edit by me: **three additive lines** on
`AssemblySessionController` (feedback clip references). No Editor command needed;
Unity generates the new `.meta` files.

### 1. Station height
`WorkspacePlacement.surfaceUpOffset` 0.01 → **0.07 m** (the component is created at
runtime, so the default applies). It lifts `MarkerAnchor`, the root of the whole
chain, so engine, trays, ghosts and the tabletop collider move together.

### 2. Side panels — why they moved, and the fix
The scene serialises the side canvases with **`m_LocalPosition` and
`m_AnchoredPosition` out of step** (e.g. StatusCanvas local `(0, 0, −0.076)` vs
anchored `(0.303, 0.2)`; ResearchCanvas local `(0, 0, −0.413)` vs anchored
`(0.62, 0.2)`). A RectTransform under a plain Transform keeps your Inspector Pos X/Y in
`anchoredPosition` and Pos Z in `localPosition`, and Unity re-derives one from the
other whenever the canvas is re-enabled or re-laid-out — which `AdaptivePanelController`
does on a support change (Hidden ⇄ shown) and the first content rebuild does after
Step 1. So the panel jumped between the two serialised poses.
`PanelRig.PinChildren()` now takes the Inspector values as truth
(`local = (anchored.x, anchored.y, local.z)`), applies them once at lock, and
`EnforcePins()` restores them every frame, logging a warning naming the panel and
the frame if anything ever moves it again. The rig itself still moves as one unit.
No per-panel offsets were added.

### 3. READ state
`AttentionFader` now dims to **28 %** (plus emission black) and, besides disabling
the Interaction SDK components, **switches off the `ISDK_*` child objects** of every
loose part, so neither hand-grab nor ray-grab can reach them; it logs
`[Attention] READ: N parts, M renderers dimmed, K components and J ISDK objects suspended`
so logcat proves it ran on the very first crankshaft instruction. Ghosts and arrow
are off while reading; Continue is the only control. Locked parts are never touched.

### 4. Tolerances, revision 2 (`StepValidator.roleTolerances`, auto-reset from the serialised table)

| Role | Position | Orientation | Symmetry |
|---|---|---|---|
| crankshaft / camshaft | 3.0 cm | 20° | axis, roll free |
| PistonHead | 2.5 cm | 22° | full |
| ConnectingRod | 2.5 cm | 22° | axis + roll mod 180° |
| ConnectingPin | 2.0 cm | 28° | axis ±, roll free |
| PistonEnd | 2.5 cm | 25° | axis + roll mod 180° |
| bolts / nuts | 2.0 cm | 28° | axis ±, roll free |
| install (partial piston → bore) | 3.0 cm | 25° | full |

Immediate snap while held is unchanged.

### 5. Assembly mat and the piston sequence
`AssemblyWorkSurface` mode `InFrontOfEngine`: 30 cm mat **in front of the engine
block towards the participant** (block footprint + 6 cm gap), **2 cm above the
detected desk**, facing the participant, with a solid slab and a **ring marking** where
the head goes (always visible with the mat — it defines the task area, not adaptive
guidance). Shown when a piston stage begins, **hidden the moment the partial piston
is installed** (`StepManager.HandleWorkflowChanged`), re-shown for the next piston.
Your `Worksurface` object under `Offset` hosts the component (set `Authored` to keep
its own transform).

Kit ghosts are posed **head flat on the mat, crown down**, rod target above it, pin
across the view. Per piston (assets regenerated, all ten enabled):

```
locate → PistonHead (mat) → ConnectingRod (from above) → ConnectingPin (from the side)
→ install: head+rod+pin to ghost.piston00N.PistonHead (mat hides)
→ PistonEnd, pistonBolt, pistonBoltOther, PistonNut, PistonNutOther at the crankshaft
   (targets ghost.piston00N.<role>, shown in isolation inside the bore ghost group)
```
Each locks and joins the bound head; the completed piston stays on the crankshaft.

### 6. L1 / L2 / L3 for pistons 2–4
Validation sequence identical at every level. Presentation only:
- **L1**: the stage's L1 block says *"Repeat the piston assembly procedure for the
  remaining pistons."*; the card shows that instead of atomic text, spoken once per
  stage; no arrow (`minimumLevel` L2), no ghosts (L1 block has none); mat + ring remain;
  the installed piston(s) are the reference.
- **L2**: atomic action text + arrow; **no ghosts** (L2 ghostKeys cleared for stages 3–5).
- **L3**: atomic text + ghost + arrow.
Piston 1 (stage 2) keeps ghosts at L2 and L3 as the scaffolded example. Crank/cam L1
override text cleared so "Find the crankshaft…" shows at L1.

### 7. Component preview
`ComponentPreview` (on the main canvas, created by `ParticipantCard`): a renderer-only
clone of the actual part mesh (shared materials, no collider/Rigidbody/SDK/registry),
10 cm, slowly spinning, 6 cm in front of the right half of the card. Source = the
action's part; for a "find the components" instruction, the next action's part; for
install, the bound head. Cleared when nothing is relevant.

### 8. Audio — what was checked, what was wrong, what changed
**The generated WAVs are valid**: RIFF/WAVE, PCM tag 1, mono, 22 050 Hz, 16-bit,
`fmt` 18 bytes, `data` sizes consistent with file sizes (all 18 audited). Unity
imported them (it accepted the pre-written metas; guids verified deterministic).

Working path (mp3): **serialized AudioClip reference** on the StepData → played via
`InstructionSpeech.PlayAuthored` → dedicated 2-D source. Failing path: `Resources.Load`
by key → the same source. Only the *reference mechanism* differed, and two import
flags: the generated metas had `preloadAudioData: 0` (inherited from the mp3 meta),
so a Resources-loaded clip had no data until `LoadAudioData()` finished — and the first
version played before that (now fixed by waiting on `loadState`, but that was never
proven on device).

Changes, in order of confidence:
1. **Every instruction is now a serialized reference**: each action's `audioCue` in the
   step assets points at its WAV (the exact mechanism that works for the mp3). The
   stage-level L1 sentence uses `l1Minimal.instructionAudio` the same way. Resources
   lookup remains only as a fallback.
2. **Feedback clips are serialized too**: `successClip`, `wrongPartClip`,
   `invalidAttemptClip` on `AssemblySessionController` (the three scene lines).
3. **`preloadAudioData: 1`** on all clip metas.
4. **Procedural tones** (`ProceduralTones`, `AudioClip.Create` in memory) as the last
   fallback for success / invalid / wrong — if a tone plays but a WAV does not, the
   asset import is the problem; if neither, the output path is.
5. Logging unchanged: `[Speech] feedback clip 'correct' … loadState=…, length=…s`.

Nine clips re-synthesised for the new wording; nine obsolete ones removed; 18 total.
**[QV]** This is the first build where generated speech goes through the proven
reference path; I cannot claim it is audible until it runs.

### 9. Spatial feedback (`FeedbackPulse`, property-block, restored exactly)
Success: green pulse on the seated part 0.8 s + success haptic + "Correct." (clip →
sentence → chime). Failed near-target attempt: amber pulse 0.4 s on the part in the
hand + warning haptic + "Not quite. Try again." (clip → tone). Wrong component:
amber pulse 0.5 s + warning haptic + the wrong-part phrase.

### 10. Error schema
`validation_attempt` now carries `error_type` ∈ {`placement_success`,
`incorrect_position`, `incorrect_orientation`, `incorrect_position_and_orientation`},
`position_ok`, `orientation_ok`, `action_id`, `requested_key`, `instance_key`,
`instance_name`, `attempt_index`, errors and tolerances, trigger; support level,
stage and timestamp are in the envelope. `wrong_component_grabbed` carries
`error_type: wrong_component`, raised on the grab transition only (one per episode).
`component_dropped` is recorded, not counted. Counted = wrong_component + failed
attempts; one per episode; a new attempt needs a zone re-entry or a re-grab.

### Files
```
new   Assets/Scripts/UI/ComponentPreview.cs
new   Assets/Scripts/UI/FeedbackPulse.cs
mod   Assets/Scripts/Steps/AttentionFader.cs       28 % dim, ISDK objects off, logging
mod   Assets/Scripts/MR/AssemblyWorkSurface.cs     mat in front, head ring, crown-down kit pose
mod   Assets/Scripts/MR/WorkspacePlacement.cs      +6 cm
mod   Assets/Scripts/UI/PanelRig.cs                pinned side panels
mod   Assets/Scripts/Steps/StepValidator.cs        tolerances rev 2, both-dimension classification
mod   Assets/Scripts/Steps/StepPresenter.cs        isolated ghost activation, L1 override speech
mod   Assets/Scripts/Steps/AssemblySessionController.cs  serialized clips, pulses, schema
mod   Assets/Scripts/Steps/StepManager.cs          mat hides after install, desk height
mod   Assets/Scripts/UI/ParticipantCard.cs         L1 override, preview
mod   Assets/Scripts/Audio/SpeechLibrary.cs        PlayFeedbackClip, ProceduralTones
mod   Assets/Scripts/Logging/SessionLogger.cs      validation_attempt fields
mod   Assets/ScriptableObjects/Steps/Step_01..06   10-action piston sequence, audio cues, level blocks
mod   Assets/Resources/AdaptiveAR/Speech/          9 new, 9 removed, preload on
mod   Assets/1 - ArUcoMarkerTracking.unity         three lines (feedback clips)
```

### [QV]
1. Engine base ~6 cm higher than before relative to the desk.
2. Side panels stay exactly where you put them after Step 1 and after B/Y changes; if
   not, logcat shows `[PanelRig] '<panel>' moved …` naming the frame.
3. First crankshaft instruction: parts dark, cannot be grabbed, no ghost/arrow; logcat
   `[Attention] READ: …`; Continue restores them.
4. Tolerances feel like "visibly right", not millimetres.
5. Mat in front of the engine just above the desk; ring; head flat; rod from above; pin
   from the side; install → mat gone; cap, two bolts, two nuts at the crank.
6. Pistons 2–4: L1 generic text + no guidance; L2 arrow, no ghost; L3 ghost.
7. Spinning part on the right of the card, changing per action, during READ too.
8. Logcat per action: `feedback clip 'correct' … length=0.8s` and an audible "Correct.";
   a chime instead means the WAV asset is the problem; silence means the path is.
9. Green flash on seat; amber flash + "Not quite" on a failed attempt.
10. JSONL: every counted error has `error_type`; drops are `component_dropped`.

Nothing in this pass has run on a headset.

---

## Claude → Reviewer — 2026-10-10 (b) — Corrective stability pass: disappearing rod, false wrong-component, audio gating, mat/head geometry, two pistons + auto-complete, startup panel, loose hardware

Base: `5cac401`. No scene edit by me (the scene diff is Unity's re-serialisation of
renamed/new fields). Side-panel offsets, materials, placement architecture untouched.
No Editor command.

### 1. Disappearing rod / assembly — root cause (traced, not patched over)

Two mechanisms combined, both on the success path of a part that snaps **while held**:

1. `StepValidator.Succeed` → snap → `PlacementLock.Lock` (kinematic, SDK components
   disabled) → `SetParent(head)`. The SDK's `RigidbodyKinematicLocker.UnlockKinematic`
   then runs when the interactor lets go — **after the lock** — and restores the
   Rigidbody's *pre-grab* state: dynamic, with a throw velocity. The old lock only
   re-asserted for 6 frames; anything later left the part dynamic.
2. `DropIntoTray.FixedUpdate` then saw a dynamic body and ran its "lost part" test:
   `parent.TransformPoint(authoredLocalPosition)`. The authored pose is tray-local
   under `piston00N`; evaluated under the **new parent (the head, scale ≈ 0.29 m per
   unit)** it is a point metres away → "wayOff" → `RecoverToAuthoredPose` set
   `localPosition = (−26.6, 54.6, −28)` in the head's frame → the rod teleported ~20 m
   away. When the head itself went dynamic for a frame (install while held), the same
   recovery (or the throw) moved the whole assembly: "entire piston disappears".

Fixes: `PlacementLock` now **enforces continuously** while locked (LateUpdate and
FixedUpdate: re-freeze if the body became dynamic, restore the local pose if it
moved, adopt a legitimate re-parent), logging each correction; `DropIntoTray` never
recovers a part whose parent changed or that carries a locked `PlacementLock`.
`PartWatch` logs name/instance id/activeSelf/activeInHierarchy/renderer/parent/pose/
kinematic at: before snap, after lock, after join, lock/unlock, any recovery. A ghost
key resolving to a grabbable (real) part is refused with an error instead of toggled.
**[QV]** — the mechanism is traced and closed; it has not run on the headset.

### 2. False "not the required component" — root cause

The validator watched every registry part key not in the accepted set. The **same
GameObject is reachable under several keys**: `part.PistonKit001` (the kit *handle*)
resolves by convention to `piston001/PistonHead` — the very head accepted under
`part.PistonKit001.PistonHead`; with interchangeable roles the heads of kits 2–4 are
accepted under their role keys while `part.PistonKit002…` aliases also resolve to
them. Grabbing an eligible head therefore also tripped the "other part" monitor.
Now: handle keys are never watched, watched objects are deduplicated, anything that
is an accepted instance is excluded by object, and kit roots (no collider) are
skipped. Every grab logs `[Eligibility] grabbed … key= requested= candidates=[…]
boundKitInstance= eligible=true/false`.

### 3. Audio gating — state machine

- Instruction enters → its serialized `audioCue` once (repeat guard 1 s).
- Wrong component → one spoken warning per **grab episode** (validator raises on the
  grab transition only) + 1.5 s cooldown in `InstructionSpeech` on top.
- Failed attempt → **no speech**; one `Invalid` tone on the attempt transition
  (`FailAttempt` fires once per zone entry / in-zone release), then
  `PlayCue` suppresses the same kind for 1.2 s and any cue for 0.5 s.
- Near but not valid → amber visual only.
- Success → `Success` chime + haptic + green pulse; **no spoken "Correct."**
  (`successClip`/`invalidAttemptClip` stay referenced but unused.)
Nothing can fire per frame: attempts are discrete, cues are cooled down.

### 4. Mat and head target geometry

Mat: `matHeightAboveDesk` **4 mm** above the placement plane (the fields were renamed so
your scene object's old `heightAboveDesk: 0.1` no longer wins), `matGapFromEngine`
2 cm from the block footprint toward you, 28 cm square. Kit ghost: head crown-down,
lifted so the **head's own** lowest point is 5 mm above the mat (previously the lift
used the whole kit, which left the head floating), rod target above, pin across the
view, ring under the head. The same spot is re-posed for every piston stage; the log
prints each kit's head target and crown height vs mat. Station offset back to
**0.01 m**; the main panel gets `extraMainPanelLift` 3.5 cm instead.

### 5. Two pistons, then auto-complete
Stages: piston 1 (scaffolded) → piston 2 (L1 "Repeat the piston assembly procedure
for the second piston."; L2 arrow; L3 ghost) → **Remaining pistons**: one
acknowledgement whose button reads **Complete Remaining Pistons →**
(`AssemblyAction.autoCompleteRemaining`). `PistonAutoCompleter` fits the two unused
real kits into their bores (locked, consumed, joined) and shows the four spare bore
ghost groups solid with the real parts' materials — six bores — then logs
`remaining_pistons_auto_completed count=6`. No attempts, errors or times for them.
Stage 5 has nothing performable and is skipped and hidden from the overview.

### 6. Startup panel
`PanelRig` parked from `Camera.main` on the first frame, when the head is still at
the origin — the floor. Now it parks only once `head.position.y > 0.4 m`, re-parks
for 0.75 s while tracking settles, then world-locks: 0.75 m ahead, 8 cm below eye
level, facing you. Never derived from the anchor or the serialized position.

### 7. Loose hardware
`DropIntoTray.ReleaseLoose()` runs for every selectable part at placement: dynamic
again, recovery counter reset, so everything settles into the trays (kit roots with
no collider and locked parts are left alone). READ dimming excludes pins, bolts and
nuts entirely and no longer disables any interaction. Instructions now agree with
behaviour: "Locate the crankshaft in the parts tray and pick it up" — picking it up
completes the instruction (`OnAcknowledgedByGrab`, logged as
`instruction_acknowledged_by_pickup`), Continue still works.

### Also
Nuts loosened to 2.8 cm / 32° (rev 3; others unchanged). Red pulse for a far-off
failed attempt, amber for close. Crank/cam preview 14 cm, embedded at the panel
plane; others 10 cm, 2 cm in front. Overview rows: ✓/●/○ workspace, crankshaft,
piston 1, piston 2, remaining pistons, camshaft (transform untouched).

### Files
```
new   Assets/Scripts/Steps/PistonAutoCompleter.cs
mod   Assets/Scripts/Steps/PlacementLock.cs           continuous enforcement + PartWatch
mod   Assets/Scripts/DropIntoTray.cs                  joined/locked guard, ReleaseLoose
mod   Assets/Scripts/Steps/StepValidator.cs           others dedupe, eligibility log, pick-up ack, rev 3, far flag
mod   Assets/Scripts/Steps/AttentionFader.cs          no interaction disable, small hardware excluded
mod   Assets/Scripts/Steps/AssemblySessionController.cs  feedback hierarchy, ack-by-grab, auto-complete
mod   Assets/Scripts/Steps/AssemblyAction.cs          autoCompleteRemaining
mod   Assets/Scripts/Steps/StepManager.cs             release loose parts at placement
mod   Assets/Scripts/Steps/StepPresenter.cs           refuse real parts as ghosts
mod   Assets/Scripts/Audio/SpeechLibrary.cs           PlayCue cooldowns, gated feedback
mod   Assets/Scripts/UI/PanelRig.cs                   valid-head park, extra lift
mod   Assets/Scripts/UI/TaskListHud.cs                progress rows
mod   Assets/Scripts/UI/ParticipantCard.cs            shortcut label, preview role
mod   Assets/Scripts/UI/ComponentPreview.cs           per-role size/depth
mod   Assets/Scripts/MR/AssemblyWorkSurface.cs        mat on desk plane, head-only lift
mod   Assets/Scripts/MR/WorkspacePlacement.cs         0.01 m
mod   Assets/Scripts/Logging/SessionLogger.cs         remaining_pistons_auto_completed
mod   Assets/ScriptableObjects/Steps/Step_01..06      locate wording, stage 4 shortcut, stage 5 off
mod   Assets/Resources/AdaptiveAR/Speech/             5 new clips, 4 removed (19)
```

### [QV]
1. Rod snaps into the head and **stays**; logcat shows `[PartWatch] Succeed(after join)` with
   activeInHierarchy=true and at most a handful of `[PlacementLock] corrected` lines.
2. Holding the right head/rod/pin/bolt/nut never says "not the required component";
   `[Eligibility] … eligible=true` on those grabs.
3. One tone per failed attempt, no "Not quite" speech; chime on success.
4. Mat flat on the desk right in front of the oil pan; head sits in the ring; rod from
   above; pin from the side; piston 2 uses the same spot.
5. After piston 2: the button fits six pistons; log line with count 6.
6. Startup panel right in front of you, not on the floor.
7. Pins/bolts/nuts in the trays at start, authored colours, grabbable during instructions.
8. Picking up the crankshaft on the first screen advances the instruction.

Nothing in this pass has run on a headset.
