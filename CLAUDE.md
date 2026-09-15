# CLAUDE.md

Guidance for Claude Code when working in this repository.

---

## 1. What this project is

A **doctoral research prototype** of an **AI-driven, context-aware adaptive mixed-reality guidance system for mechanical assembly**, running on **Meta Quest 3** (passthrough MR, hand tracking, ArUco marker anchoring).

The research question concerns *adaptive instructional support*: the system observes user and task context and uses an AI decision layer to select among **predefined levels of instructional support** while the operator assembles a V8 engine model.

This is a research instrument, not a product. Reproducibility, traceability of decisions, and clean experimental logging matter more than polish or feature breadth — but see §4: the deliverable is a **working headset system**, not a set of backend scripts.

---

## 2. Research architecture (design constraints — these are not negotiable)

### 2.1 The AI is a *selector*, never an *author*

> **The AI must NOT generate mechanical assembly instructions.**
> All instructional content is **researcher-authored and predefined**.

The AI decision layer may only emit one of a **constrained action set**:

| Action | Meaning |
|---|---|
| `KEEP_SUPPORT` | Remain at the current support level |
| `INCREASE_SUPPORT` | Move one level toward more assistance |
| `DECREASE_SUPPORT` | Move one level toward less assistance |

Any output outside this set is a **constraint violation** and must be detected, logged, and handled by a safe fallback (default: `KEEP_SUPPORT`). Constraint compliance is itself a measured outcome of the evaluation — do not silently coerce or "fix up" malformed model output without logging that it happened.

### 2.2 Instructional support levels

Actions select among three predefined levels:

| Level | Name |
|---|---|
| **L1** | Minimal Support |
| **L2** | Guided Support |
| **L3** | Assisted Support |

**The exact contents of L1/L2/L3 and the transition policy will be defined separately from the literature and must remain configurable** (ScriptableObject / config asset / JSON — not C# constants). Do not invent level contents, thresholds, or transition rules, and do not hard-code psychological assumptions that the literature review has not yet supplied.

### 2.3 Decision inputs

The context vector fed to the decision layer will eventually include:

- current assembly step
- predefined task complexity (researcher-authored per step)
- operator experience level
- elapsed time on current step
- failed placement attempts / errors
- current assistance level
- HRV-derived physiological state

Inputs may be added incrementally. Design the context model so absent inputs are explicitly representable (e.g. "HRV unavailable") rather than defaulted to a fabricated value.

### 2.4 HRV is evidence, not a trigger

HRV is **contextual physiological evidence** that enters the decision layer alongside task state.

> **Do NOT implement a rule of the form "HRV value X ⇒ support level Y."**

No direct HRV→level mapping, no hard-coded stress thresholds driving instruction changes, no implicit assumption that a particular HRV pattern means a particular cognitive or affective state.

---

## 3. Experimental / evaluation architecture

**Primary evaluation:** a **controlled scenario-based technical evaluation of the AI decision framework** — not a large human study. It measures:

- decision consistency (same context ⇒ same decision)
- agreement with a reference policy
- robustness (degraded, noisy, missing, or adversarial inputs)
- constraint compliance (output stays inside the allowed action set)
- decision latency
- failure analysis

This implies the decision layer must be **runnable headlessly, offline, and deterministically replayable** from recorded or synthetic context vectors, independent of the Quest build.

**Secondary (possible) evaluation:** a **small human feasibility / usability evaluation** of the integrated Quest 3 system. Do **not** build assumptions that require a large controlled human experiment (no counterbalancing infrastructure, no power-analysis-driven condition machinery, no multi-arm randomisation framework unless explicitly requested).

### 3.1 Required log record

Every decision event must eventually log:

- participant / session ID
- timestamp
- module / assembly step
- task-state inputs
- physiological-state input
- current support level
- AI input (the exact context passed to the model)
- AI decision (raw + parsed)
- resulting support level
- AI response latency
- errors / failed attempts
- step completion time

Logging must be **reproducible**: stable schema, stable field order, explicit units, explicit "missing" markers, and a schema/version identifier in the file so old runs remain interpretable.

---

## 4. Final integrated system requirement

> The end goal is a **complete, physically testable Meta Quest 3 MR research prototype — not merely backend scripts.**

All of the following must ultimately operate as **one integrated system running on the headset**:

- Unity scene and world-space UI
- physical/digital alignment and ArUco anchoring
- assembly interactions (grab, place, release)
- instructional-support levels L1/L2/L3
- visual / ghost guidance
- task validation
- physiological sensing
- AI decision layer
- experimental logging

§3's headless-replay requirement and this requirement are **both** binding: the decision layer must be independently testable *and* must actually drive the in-headset experience. Neither substitutes for the other.

### 4.1 In-headset usability is a requirement, not polish

UI and guidance must be **appropriately positioned, readable, and usable in-headset** — at real working distance, over passthrough, while the operator's hands are busy, without occluding the workpiece. Canvas placement and distance, text size and contrast, follow/billboard behaviour, and guidance visibility against a cluttered physical bench are **functional requirements**, and they are validated in the headset, not in the Editor Game view.

### 4.2 Compilation is not validation

> **Do not assume successful compilation means successful MR behavior.**

Editor play mode does not reproduce passthrough camera intrinsics, real marker detection under real lighting, hand tracking, physical scale, occlusion, or frame timing. Any change touching tracking, anchoring, alignment, interaction, guidance, or UI placement is **unverified until it has run on a Quest 3**. Say so plainly when reporting such work: "compiles, not yet device-verified."

### 4.3 Preserve and extend on-device debugging and calibration

The prototype must stay **iteratively testable and calibratable on hardware**. Do not remove or bypass existing debug/calibration affordances, and provide equivalents for each new subsystem:

- the **CV debug quad + `OVRInput.Button.One` toggle** (raw ArUco detection view) — existing, keep working
- an **on-device state readout**: current step, support level, marker/detection state, decision latency, physiological-stream status, logging status. `StatusCanvas` already exists for exactly this (§7.4) — use it rather than building a new one
- a way to **exercise state transitions without a full assembly run** (the `MultiInputTrigger` shim is the existing precedent for this)
- **calibration/alignment adjustable in-headset** rather than requiring a rebuild to nudge a value
- **logs retrievable from the device** (`Application.persistentDataPath` → `adb pull`) and readable without the Editor

Prefer inspector-exposed or config-driven parameters over recompilation for anything a researcher will want to tune during a physical test session.

### 4.4 Build in vertically testable slices

Prefer changes that leave the system **runnable on-device at every step**. A subsystem that cannot yet be exercised in the headset should ship behind a toggle with a mock or stub, not as unreachable code. Keep the scene launchable and the existing working flow (§7.2) intact throughout.

---

## 5. Engineering constraints

1. **Preserve the currently functional Quest 3 passthrough and ArUco marker tracking.** These work on-device; treat them as load-bearing.
2. **Do not rewrite working systems unless explicitly approved.**
3. **Make minimal, incremental changes.** Prefer additive new files over edits to functioning ones.
4. **Do not remove legacy packages or assets yet** (Vuforia, ARCore, XRI, OpenCV examples, sample scenes, `day1.apk`) even though they are unused — removal is a separate, explicitly approved task.
5. **Preserve Quest 3 / Android compatibility**: IL2CPP, ARM64, OpenXR loader, no `UnityEditor` references in runtime scripts, no desktop-only APIs.
6. **Experimental behavior and logging must be reproducible.**
7. **Keep the AI provider/model behind an abstraction** (e.g. `IDecisionProvider` / `IAdaptationPolicy`) so the model can be swapped — and so a deterministic mock/reference policy can be substituted for offline evaluation — without rewriting the experiment.
8. **Never commit API secrets.** No keys in source, scenes, ScriptableObjects, or `ProjectSettings`. Use an untracked local config or environment-supplied value, and add the path to `.gitignore` in the same change.
9. **Explain before major architectural or Unity scene changes.** Scene YAML edits are hard to review and easy to corrupt — describe the proposed change and get agreement first.
10. **Use existing UI/scene assets where practical** rather than rebuilding. `StatusCanvas` and `OverviewCanvas` already exist and are unwired (§7.4) — they are the natural surfaces for state readout, timer, progress, and error display.

---

## 6. Environment

| | |
|---|---|
| Unity | **6000.0.32f1** (Unity 6 LTS), URP |
| XR loader (Android + Standalone) | **OpenXR** (`Assets/XR/Loaders/OpenXRLoader.asset`). The Oculus loader asset exists but is *not* in the loader list |
| Meta SDK | `com.meta.xr.sdk.all` **83.0.1** (OVRCameraRig, OVRManager, OVRPassthroughLayer, Interaction SDK) |
| CV | OpenCV for Unity (EnoxSoftware), full package + examples |
| Build | Android, IL2CPP, ARM64, min/target SDK 32, HorizonOS SDK 60→83 |
| Device | **Quest 3 only** (`com.oculus.supportedDevices = quest3`) |
| Permissions | `HAND_TRACKING`, `USE_ANCHOR_API`, `USE_SCENE`, **`horizonos.permission.HEADSET_CAMERA`** (required for Passthrough Camera API), PASSTHROUGH required |
| Product | `AdaptiveARPrototype` |
| Repo | git; working branch `ai-prototype-sprint`, main branch `main` |
| Assembly | No `.asmdef` in project code — everything lives in `Assembly-CSharp` |

Platform note: the dev machine is **Windows**; PowerShell is the primary shell, Bash is also available.

---

## 7. Current project state

The single source of truth for the live prototype is the one enabled build scene:

> **[1 - ArUcoMarkerTracking.unity](Assets/1%20-%20ArUcoMarkerTracking.unity)**

Two older scenes ([Scenes/main.unity](Assets/Scenes/main.unity), [DemoScene.unity](Assets/DemoScene.unity)) are earlier iterations. Several scripts exist *only* in those scenes and are therefore **not live**.

### 7.1 Runtime flow (live scene)

```
OVRManager ── requests passthrough + HEADSET_CAMERA permission on startup
      │
PassthroughCameraAccess ──GetTexture()──┐      ──GetCameraPose()──┐
                                        ▼                          ▼
              ArUcoTrackingAppCoordinator.Update()          CameraAnchor transform
                     │  DetectMarker(tex, resultTex)                │
                     ▼                                              │
              ArUcoMarkerTracking  (OpenCV: detect → solvePnP → LPF)│
                     │  EstimatePoseCanonicalMarker(dict, anchor) ◄─┘
                     ▼
              EngineAnchor.transform   (driven by marker id 0; holds the 3D engine model)
                     ▲                          resultTex ──► Camera Image Visualizer (debug quad)
                     │ SetActive / freeze
              StepManager ──(reflection)──► ArUcoMarkerTracking._detectedMarkerIds
                     ├──► DemoUICanvas/InstructionPanel/CaptionText
                     ├──► GotItButton (listener added in code)
                     └──► Instantiate(crankshaftGhostOverlay)
                                              OVRInteractionComprehensive
                                                │ ray + hand-grab
                                                ▼
                                   Grabbable parts, PointableCanvas UI
                                   DropIntoTray → gravity into trays
```

### 7.2 ✅ Confirmed working on-device — do not break

**The end-to-end launch flow runs on a Quest 3 today:**

> launch → look at the ArUco marker → the **oil pan / engine model anchors** to the physical marker → **all other engine parts appear in the trays beside it** → the **first instruction step becomes active**.

This is the baseline every change must preserve. It is the concrete meaning of constraint 1.

Component parts of that flow:

- **Quest 3 passthrough** (`OVRPassthroughLayer` underlay, `isInsightPassthroughEnabled`, floor-level tracking origin).
- **Passthrough Camera API access** via `Meta.XR.PassthroughCameraAccess` (`Passthrough Camera Access` GameObject) with the correct manifest permissions.
- **ArUco detection + pose estimation** — [ArUcoMarkerTracking.cs](Assets/Scripts/ArUcoMarkerTracking.cs). The strongest code in the repo: intrinsics matrix construction, intrinsics rescaled to `CurrentResolution`, downscale-by-`_divideNumber` (2) for performance, ArUco3 + sub-pixel corner refinement, `solvePnP`, and a per-marker-ID low-pass filter (`Lerp`/`Slerp` at 0.5). Live config: `DICT_4X4_50`, `_markerLength = 0.055 m`.
- **Camera-pose → world-space chain** — [ArUcoTrackingAppCoordinator.cs](Assets/Scripts/ArUcoTrackingAppCoordinator.cs) creates a runtime `CameraAnchor`, updates it from `GetCameraPose()` each frame, and maps `{ markerId 0 → EngineAnchor }`.
- **`EngineAnchor` is the anchored root that holds the 3D engine model** — the oil pan, the `Components` subtree (crankshaft, camshaft, pistons, block sections) staged in the two trays, and the `Ghosties` target-pose overlays. Revealing `EngineAnchor` is what makes the model and the tray parts appear together, correctly registered to the physical marker.
- **Physical/digital alignment strategy** — anchor on marker detection, then `arucoCoordinator.enabled = false` to freeze the pose and eliminate jitter for the rest of the session.
- **First instruction step activation** — `StepManager` reveals the anchor, sets the caption, and spawns the crankshaft ghost overlay.
- **CV debug visualiser** — [CameraImageAduster.cs](Assets/Scripts/CameraImageAduster.cs) scales a quad by real focal length so the debug image overlays passthrough at correct angular size. `OVRInput.Button.One` toggles debug view vs. AR objects. **This is the primary on-device tracking debug tool — keep it.**
- **Meta Interaction SDK interaction** — `OVRInteractionComprehensive` rig; world-space canvases made interactable via `PointableCanvas` + `RayInteractable` + `ISDK_RayCanvasInteraction`; `Grabbable` + `ISDK_HandGrabInteraction` on the crankshaft and the `Components` root.
- **[DropIntoTray.cs](Assets/Scripts/DropIntoTray.cs)** (on 74 parts) — kinematic while hidden, released to gravity on `OnEnable` so parts settle into the tray meshes.
- **A built APK exists** (`day1.apk`, repo root) from this pipeline.

### 7.3 ⚠️ Live, working, but fragile

- **[StepManager.cs](Assets/Scripts/Steps/StepManager.cs)** — the only live step logic, and the driver of the §7.2 flow. A hard-coded sequence: `Start()` hides the anchor and shows Begin → `StartSession()` → `Update()` polls for marker 0 → `ActivateAndLockBlock()` reveals `EngineAnchor` and freezes tracking → `SpawnParts()` instantiates `crankshaftGhostOverlay` and sets the first instruction caption. **Then it stops** — there is no step 2, no completion check, and no advance logic. This is the extension point for the L1/L2/L3 system, and the main thing standing between the current prototype and §4.

  Naming note, so it is not "fixed" by mistake: the inspector field is called `oilPan` but is wired to **`EngineAnchor`**. That is **correct and intentional** — `EngineAnchor` is the root holding the whole engine model, and activating it is what brings up the oil pan *and* the tray parts together. Leave the wiring alone; only the field name is legacy.

  Genuine fragilities, worth addressing when that area is touched anyway:
  - reads `ArUcoMarkerTracking._detectedMarkerIds` **by C# reflection** every frame (no public accessor exists); brittle under IL2CPP and a silent-failure risk on device
  - no confidence or persistence gate — a single spurious detection latches the anchor permanently, with no in-headset way to re-anchor short of relaunching
  - once the coordinator is disabled, tracking never resumes (no re-acquisition, no drift correction, no operator-triggered recalibration) — relevant to §4.3
- **Broken UnityEvent wiring in the live scene** — 6 of 7 serialized button bindings point at missing methods or null targets: `GotItButton → StepManager.StartStep1` (missing), `ConfirmPlacement → StepManager.ConfirmPlacement` (missing), and `CalibrationButton ×2` / `ContinueButton ×2` / `ConfirmCalibrationButton` / `ResetCalibrationButton` (null targets, left behind when calibration components were removed from the scene). Only StepManager's code-added listener actually fires; the flow works *despite* this wiring, not because of it.
- **[MultiInputTrigger.cs](Assets/Scripts/MultiInputTrigger.cs)** (`InputManager` GameObject) — mouse / Space / Quest index trigger all invoke `gotItButton.onClick`. A deliberate test affordance; the precedent for §4.3's "exercise transitions without a full assembly run".

### 7.4 ❌ Not yet built in this repo

- **No data logging of any kind.** No `StreamWriter`, `persistentDataPath`, CSV/JSON writer, session recorder, or timestamping anywhere in project code — only 16 unstructured `Debug.Log` calls, visible solely over logcat. **This is the single biggest gap** relative to §3.1, and it also blocks §4.3's "logs retrievable from the device".
- **Physiological sensing — not in this repo, but *not* a from-scratch build.** This project has zero hits for `bluetooth`, `BLE`, `heart`, `HRV`, `polar`, `empatica`, `shimmer`, `GSR`, `EDA`; no BLE plugin and no Android Bluetooth permission in the manifest.

  > **A working BLE layer already exists in a separate Unity project.**

  Treat HRV acquisition as a **port-and-integrate** task. Before designing any BLE stack: ask for that project's source, preserve its working transport and parsing rather than reimplementing, and adapt it behind a provider interface (e.g. `IPhysiologicalSource`) so a recorded or mock stream can be substituted for bench testing and for the §3 offline evaluation. Porting it will require **manifest changes** (Android Bluetooth / location permissions) — that is a §5.9 "explain first" change, and it affects the Quest build, so it needs device re-verification per §4.2.
- **No AI decision layer and no adaptation logic** of any kind yet.
- **[StepData.cs](Assets/ScriptableObjects/Steps/StepData.cs) and its two assets are orphaned.** A proper step definition (`stepTitle`, `stepDescription`, `ghostPrefab`, `instructionAudio`, `arrowPrefab`) with `Step_0_Demo` and `Step_1_Crankshaft` authored — **but nothing in C# loads them.** They are referenced only as inspector fields in the two dead scenes. This is the obvious foundation for researcher-authored L1/L2/L3 content.
- **`StatusCanvas`** — `TimerText/TimeValue`, `ErrorText/ErrorValue`, `ProgressText/ProgressValue`, `ProgressBar` all laid out, **no script drives any of it**. Intended surface for the §4.3 on-device state readout.
- **`OverviewCanvas`** — `TaskA/TaskB/TaskC` rows, Next/Back, `CalibrationPanel` (inactive), **no script drives it**.
- **[SnapToPosition.cs](Assets/Scripts/SnapToPosition.cs)** — the intended task-validation mechanism. Not in the live scene, and defective: starts a fresh coroutine every frame; `positionThreshold` defaults to `10.0f` so the position gate is effectively always true once within 0.1 m; compares `Vector3.Distance` on Euler angles (wraps badly near 0/360); and **imports `UnityEditor`, which will break the Android player build** if it ever enters the build set. Task validation and the "failed placement attempts / errors" decision input both depend on replacing or repairing this.
- **Four dormant calibration systems, none in the live scene** — relevant to §4.3's in-headset calibration requirement: [ManualBlockCalibration.cs](Assets/Scripts/MR/ManualBlockCalibration.cs) (pinch-grab reposition + confirm/unlock — the most complete, and the closest thing to an in-headset alignment nudge), [CalibrationManager.cs](Assets/Scripts/Tracking/CalibrationManager.cs) (3 s countdown, two-hand midpoint offset capture), [WorkspaceCalibrator.cs](Assets/Scripts/Core/WorkspaceCalibrator.cs) (place assembly 0.5 m ahead at y=0.75), [HandPinchProxy.cs](Assets/Scripts/XR/HandPinchProxy.cs) (thumb/index midpoint proxy).
- **[PalmPoseProvider.cs](Assets/Scripts/Tracking/PalmPoseProvider.cs)** — not in any scene, but computes hand linear velocity, per-frame angular delta, midpoint, and inter-hand distance. A useful primitive for behavioural metrics.
- **[ChArUcoMarkerTracking.cs](Assets/Scripts/ChArUcoMarkerTracking.cs) / [ChArUcoTrackingAppCoordinator.cs](Assets/Scripts/ChArUcoTrackingAppCoordinator.cs)** — a complete, more accurate board-based alternative (`CharucoBoard` + `CharucoDetector`). Compiles, in no scene. A ready fallback if marker accuracy or anchoring stability becomes a problem on the bench; not currently wired.
- **Legacy phone-AR input** — [DragPiston.cs](Assets/Scripts/DragPiston.cs) (mouse/touch raycast drag), [modelInteraction.cs](Assets/Scripts/modelInteraction.cs) (1/2/3-finger rotate/zoom/pan), [DemoManager.cs](Assets/Scripts/DemoManager.cs), [EngineModelToggle.cs](Assets/Scripts/EngineModelToggle.cs), [HideButton.cs](Assets/Scripts/HideButton.cs). Dead in the live scene.
- **Unused installed packages/assets** (keep per constraint 4): Vuforia 11.4.4 + `NonAdaptiveARPrototype` dataset + `VuforiaLicense.cs`, `com.unity.xr.arcore`, XRI 3.0.7, XR Hands, OpenCV example ONNX models, `yolo12nNoNMS.sentis` + `com.unity.ai.inference`, ~40 sample scenes. None are referenced by any live scene or project script.

### 7.5 Live scene hierarchy (abridged)

```
OVRCameraRig (prefab)              isInsightPassthroughEnabled=1, trackingOrigin=FloorLevel
  └─ .../OVRInteractionComprehensive (prefab)    ← hands + controllers
[BuildingBlock] Passthrough        OVRPassthroughLayer (underlay)
Passthrough Camera Access          Meta.XR.PassthroughCameraAccess
ArUcoMarkerTracking                ArUcoMarkerTracking.cs
'[ApplicationCoordinator]'         ArUcoTrackingAppCoordinator.cs  { markerId 0 → EngineAnchor }
Camera Image Visualizer            CameraImageAduster.cs      ← CV debug quad (Button.One)
StepManager                        StepManager.cs
InputManager                       MultiInputTrigger.cs       ← test-transition shim
EngineAnchor                       ← marker-anchored root; HOLDS THE 3D ENGINE MODEL
  └─ Offset
       ├─ tray, tray (1)           ← parts staged here beside the engine
       ├─ Components               crankshaft (Grabbable+Rigidbody+MeshCollider+DropIntoTray),
       │                           camshaft, piston001-004, engineBlockSep001-004
       └─ Ghosties                 oilPan (active) + crankshaft/pistons/engineBlockFront (INACTIVE,
                                   pre-placed at target poses — the per-step ghost guidance)
DemoUICanvas                       InstructionPanel/CaptionText, ButtonsPanel/GotItButton, ...
OverviewCanvas                     (unwired — task list surface)
StatusCanvas                       (unwired — state readout surface)
CrankshaftMarker, AudioSource, Directional Light, EventSystem
```

---

## 8. Working conventions

- **Read before you write.** Scene YAML is large; use targeted `grep`/`sed` rather than opening whole scenes.
- **Prefer new files** under `Assets/Scripts/<Area>/` over edits to functioning scripts. Existing areas: `Steps/`, `Tracking/`, `Core/`, `MR/`, `XR/`.
- **No `UnityEditor` in runtime scripts** — it breaks Android player builds (see `SnapToPosition.cs` for the cautionary example).
- **Decision-layer code must not depend on Unity scene state.** Keep the policy/provider layer plain C# and injectable so it can run headless for the §3 technical evaluation — while still being wired into the live scene per §4.
- **Log the decision, not just the outcome.** The AI input, raw response, parsed action, latency, and any constraint violation are all research data.
- **Anything not yet specified by the literature review stays configurable**, not constant. Same for anything a researcher will want to tune mid-session on the bench.
- **State device-verification status when reporting work.** Distinguish "compiles", "runs in Editor", and "verified on Quest 3" — never imply the last from the first two (§4.2).
- **Every change should leave the §7.2 flow intact and the scene launchable.** If it can't, say so before making it.
- When unsure whether a change counts as "major" (scene edits, new managers, changed tracking or anchoring behavior, manifest/permission changes, package changes): **explain first, then act**.
