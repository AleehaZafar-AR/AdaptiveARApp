# Human pilot build notes

Study-ready build of the AdaptiveARApp prototype for the human pilot. One intentional
functional change against the frozen runtime; the technical-validation layer compiled out.

| | |
|---|---|
| Branch | `human-pilot-final` (from `hrv-integration` head `b1f43bb`) |
| Frozen decision runtime | `0b918b1` (Level 1 evaluated; TECHNICAL_VALIDATION.md, eval_out/full_live/final_report.md) |
| Code built | `d759bd2` plus the uncommitted working-copy inputs listed below |
| Build date | 2026-11-03 15:15 (UTC-6), batch mode |
| Unity | 6000.0.57f1, Android, IL2CPP, ARM64, OpenXR, min/target SDK 32 |
| Build profile | `Assets/Settings/Build Profiles/HumanPilot.asset` (no Player Settings overrides, global scene list, scripting define `ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION`) |
| APK | `Builds/AdaptiveAR_HumanPilot_Final.apk` (git-ignored; 146,747,384 bytes) |
| APK sha256 | `3d880eb4ffb2c2df8d0b7752b0031f784565626eaef677443c0a44553ad217ec` |
| Package / label / version | `com.vuforia.engine.VuforiaEngine` / `AdaptiveARPrototype` / 11.4.4 (versionCode 1144) |
| Device verification | **not yet performed** (no Quest attached at build time); see the smoke-test list |

## 1. Baseline

`0b918b1` ("Provider reliability: send thinking.type=between_tools") is the frozen,
Level-1-evaluated decision runtime. Every later commit on `hrv-integration`
(`7eba1c5` … `b1f43bb`) is additive technical-validation work: the
`Assets/Scripts/TechnicalValidation/` folder, scenario JSON under
`Assets/Resources/TechnicalValidation/`, headless tests, `tv.ps1`, `TECHNICAL_VALIDATION.md`,
and four additive `TechnicalValidation*` entry points at the end of `StepValidator.cs`. No
production fix, decision, support, physiology, logging, prompt, scenario or scene change
exists after `0b918b1`, so `0b918b1 + inert layer` is the participant runtime, and the pilot
branch starts from `b1f43bb` rather than from `0b918b1` so the validation tooling stays in
the history.

## 2. Diff versus the frozen baseline (`0b918b1..d759bd2`)

Production code:

- `Assets/Scripts/Steps/StepValidator.cs`
  - (from the validation commits) four additive methods `TechnicalValidationSucceed`,
    `TechnicalValidationFailAttempt`, `TechnicalValidationWrongComponent`,
    `TechnicalValidationTryGetPlacement` and the read-only `TechnicalValidationArmedActionId`.
    Nothing in the participant flow calls them; in this build their only caller does not exist.
  - (pilot change, commit `5bd73e7`) the piston-head symmetry rule described in §3.

Inert in the player (present in source, compiled out or never instantiated):

- `TechnicalValidationBootstrap.Create` is inside `#if !ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION`;
  the HumanPilot profile defines that symbol, so the runner, overlay, synthetic physiology and
  config-file lookup are **absent from the player**. Verified on the APK: the IL2CPP string
  metadata contains neither `[TechnicalValidation] off: no ` nor `[TechnicalValidation] ENABLED: `
  (both live only in `Create`), while production literals (`AdaptiveAR/sessions`,
  `adaptivear.session.v2`, the role-table reset message) are present.
- `TechnicalValidationRunner`, `Overlay`, `Config`, `ValidationScenario`, `Level2Summary`,
  `CompletionSequencer`, `PlacementMotion`, `PresentationPlacementAnimator`: compiled classes with
  no instance and no caller.
- `Assets/Resources/TechnicalValidation/TV1..TV6.json`: bundled TextAssets, read only by the
  compiled-out bootstrap.

Tooling and docs only (never in the APK): `Tools/PhysiologyTests/TechnicalValidationTests.cs`,
`Tools/TechnicalValidation/*`, `TECHNICAL_VALIDATION.md`, `Assets/Editor/HumanPilotBuild.cs`,
`Assets/Editor/Tests/PistonHeadSymmetryTests.cs`, the HumanPilot build profile, this file.

Uncommitted working-copy inputs that were part of the build (the researcher's local state,
deliberately not committed; `git diff` fingerprint sha256
`257562b6d3b9a774be3aed3828de09e3a0be6dd38a08050e39570d1547d77953`, 4 files, +1759/-143):

- `Assets/1 - ArUcoMarkerTracking.unity`: the live scene (authored head pose, mat layout,
  per-role axis fields, panel positions). Last committed 2026-10-10.
- `ProjectSettings/ProjectSettings.asset`: app icon only.
- `Assets/Settings/Build Profiles/Quest3.asset`: a Player Settings override with product name
  `TechnicalValidation` and a different icon. **Not used** by this build (see §4).
- `Assets/videoTexture.renderTexture`: 5 lines, unrelated.

To rebuild byte-for-byte later, commit the scene (and icon) on this branch first; without that
the commit hash alone does not reproduce the APK.

## 3. Intentional change: piston head accepted either way round on the mat

Problem: the action "Place one piston head flat on the marked assembly area" (steps 2 and 3,
part role `PistonHead`, target `ghost.PistonKitNNN.PistonHead`) required the full authored
orientation. A head lying flat but turned 180° about its vertical axis, with the wrist-pin
opening facing the other way, is mechanically equivalent at this stage and was rejected.

Implementation (`StepValidator.cs`, role-tolerance table revision 8, commit `5bd73e7`):

- `PistonHead` role entry: `symmetry = HalfTurnAboutAxis`, `axis = Shortest`. Position
  tolerance 0.020 m and orientation tolerance 22° unchanged. The revision bump (7 → 8) makes
  `Awake` reset the serialized table to the code defaults, as it already did for revision 7, so
  no scene edit is needed.
- The rule is the existing one used by the rod end cap: the part is judged against the nearer of
  A = authored target rotation and B = A turned 180° about the part's physical symmetry axis.
  A 90° turn, an upside-down pose (180° about a horizontal axis) and a sideways pose are as far
  from acceptance as before (90°, 180°, ≥ the tilt angle).
- Axis: the piston-head mesh (`Assets/Models/piston/piston_-_scaled (1).fbx`) is a short cylinder,
  bounds 35.18 × 17.78 × 35.18 mesh units; the two equal extents are the skirt diameter, so the
  axis of revolution (the bore axis, vertical when the head lies flat on its crown) is the
  shortest extent, mesh-local Y. `AxisSelect.Shortest` selects it from the model itself, the same
  mechanism that picks a nut's insertion axis. It is not derived from the assembled ghost (where
  the rod is angled) and not a hard-coded axis.
- Position: the validator compares geometric centres, and the head's centre lies on the bore
  axis, so the half turn leaves the position error unchanged; the pivot is off-axis, which is
  why pivots are not compared.
- Snap/lock keep the accepted orientation (as for the cap), and the kit's rod and pin targets
  follow the head (`FollowKitHandle`), rotating about the same axis, on which they are centred.
- Scope: only actions whose part role is `PistonHead` are affected, i.e. the mat placement for
  piston 1 and piston 2 (and the disabled piston-4 step). The pick-up action has no validation.
  The "fit the piston assembly into its cylinder bore" action keys on the kit root
  (`part.PistonKitNNN`, no role entry) and keeps full orientation matching. No other role entry
  changed (test `RoleTable_OtherRoles_UnchangedFromRevision7`).
- Code touched beyond the table: the assembled-ghost axis lookup now runs only for the
  assembly-relative axis modes (cap → head, bolt ↔ nut), which is what every existing role used;
  the half-turn and extent-axis maths are exposed as pure static functions
  (`HalfTurnVariants`, `HalfTurnOrientationError`, `ExtentAxisFor`) and routed through them with
  the same values. Attempt and error accounting is untouched; the symmetric orientation error
  simply feeds the existing gate and classifier.

## 4. How the APK was built

`Assets/Editor/HumanPilotBuild.cs` (menu `AdaptiveAR > Build > Human pilot APK`, or
`-executeMethod AdaptiveAR.EditorTools.HumanPilotBuild.Build`) builds with the committed
`HumanPilot` build profile: Android platform settings copied from the committed Quest3 profile,
no Player Settings override (so the global ProjectSettings product name applies), the global
scene list (checked to enable exactly the live scene), and the compile-out define. The Quest3
profile's uncommitted override would have labelled the app "TechnicalValidation".

This APK was produced in batch mode from a sibling copy of the project because the Editor had
the project open; the copy was deleted afterwards. Rebuilding from the Editor menu on this
branch gives the same configuration.

## 5. Validation status

Static and headless (done):

- Runtime and editor assemblies type-check (csc) with the change.
- Headless suite `Tools/PhysiologyTests`: 232 passed, 0 failed (unchanged by this work).
- Edit Mode suite `AdaptiveAR.EditorTests.PistonHeadSymmetryTests`, batch mode, Unity
  6000.0.57f1: 13 passed, 0 failed. Cases: real mesh axis is the shortest extent (Y); role table
  PistonHead entry and all other entries; canonical pass; half turn pass as variant B; half turn
  ± misalignment inside/outside 22°; quarter turn fail; upside-down fail (180° from both
  variants); sideways fail; centre invariant under the half turn; valid orientation outside the
  2 cm position tolerance fails; rod end cap maths unchanged.
- APK inspection (aapt + metadata strings): label, package, version, ARM64, permissions
  (`HAND_TRACKING`, `HEADSET_CAMERA`, `BLUETOOTH_SCAN`, `BLUETOOTH_CONNECT`, `INTERNET`);
  bootstrap compiled out.
- Configuration inspection: every step asset `defaultSupportLevel: 1` (L1 Minimal);
  `Assets/Resources/Decision/decision_config.json` unchanged since `0b918b1` (provider `llm`,
  `anthropic` / `claude-sonnet-5-5`, `maxTokens 16`, `timeoutMs 8000`, cooldown 15 s); API key
  only from `persistentDataPath/AdaptiveAR/config/ai_provider.local.json` on the device;
  `PhysiologyConfig` enabled, source PolarVeritySense; session JSONL schema `adaptivear.session.v2`
  written to `persistentDataPath/AdaptiveAR/sessions/<participantId>_<UTCstamp>.jsonl`.

On-device (**not done**; the smoke test must be run on a Quest 3 before the pilot):

1. `tv.ps1 status` / `tv.ps1 off` (hygiene only: the pilot build ignores the file regardless).
2. Install: `adb install -r Builds/AdaptiveAR_HumanPilot_Final.apk`; confirm the app label reads
   AdaptiveARPrototype and the logcat start-up shows no `[TechnicalValidation]` line at all.
3. Confirm `ai_provider.local.json` is present on the device (`adb shell ls
   /sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/config/`).
4. Sensor set-up screen reaches "ready" with the Polar Verity Sense; `physio_connection` and
   `physio_window` lines appear in the session JSONL.
5. Every step starts at L1 (`support_level` on each `step_start`), decisions arrive from the
   provider (`decision` lines with latency, no fallback), and the full assembly reaches
   `session_complete`.
6. Piston head: place it flat canonical → accepted; place it flat turned 180° about the vertical
   → accepted, and the logcat `[PistonHeadValidation] variant=B` line appears; then confirm the
   rod and pin still validate on that head and the piston installs. Try a 90° turn and a
   crown-up pose → not accepted (feedback "turn to match").
7. `adb pull /sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/sessions/`
   and check the file opens as JSONL with the expected schema.

## 6. Known limitation (unchanged frozen behaviour)

The participant id is the `SessionLogger.participantId` inspector field in the scene (`P00`);
there is no in-app entry and no device-side override. Session files are distinguished by the
UTC timestamp in the file name. Changing the id requires editing the field in the scene before
a build (or a later, explicitly approved runtime override).
