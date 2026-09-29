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
