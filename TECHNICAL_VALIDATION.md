# Level 2: automated in-engine / Quest technical validation

Level 1 (`DECISION_LAYER.md`, `eval_out/full_live`) measured the decision layer headlessly.
Level 2 verifies that scripted participant events flowing through the **actual Unity/Quest
application** produce the correct end-to-end behaviour:

```
scripted participant event
  -> production StepValidator outcome / Continue control      (same events a real placement raises)
  -> AssemblySessionController: task-event ledger, counters, feedback
  -> DecisionScheduler + RequestGate                            (unchanged, real time)
  -> LlmDecisionProvider -> Anthropic claude-sonnet-5-5         (real calls)
  -> structural constraints (ApplyDecision) -> SupportLevelController
  -> StepPresenter / AdaptivePanelController / audio            (the real L1/L2/L3 presentation)
  -> SessionLogger                                              (the normal production JSONL)
```

The runtime under validation is the frozen, Level-1-evaluated build (commit `0b918b1`). The
Level 2 layer adds nothing to the decision architecture and is **inert in participant builds**.

## Isolation: how validation mode is kept out of participant sessions

| Mechanism | Effect |
|---|---|
| Activation file | `TechnicalValidationBootstrap` runs once after the first scene loads and returns immediately unless `<persistentDataPath>/AdaptiveAR/config/technical_validation.json` exists **and** contains `"enabled": true`. Without it, no runner, no overlay, no synthetic physiology and no injected event exist. The only trace is one `[TechnicalValidation] off` log line. |
| Compile-time kill switch | Builds can define `ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION` to compile the bootstrap out entirely. |
| Loud labelling | When enabled: a `LogWarning` at start-up, a magenta **TECHNICAL VALIDATION** overlay, the production logger's participant id set to `TV_<scenario>` with condition `technical_validation`, and `technical_validation_started` / `tv_event` / `technical_validation_completed` notes in the production JSONL. A contaminated session cannot pass as a participant session. |
| Production entry points | The one production file touched is `StepValidator.cs`, which gained three **additive** methods (`TechnicalValidationSucceed`, `TechnicalValidationFailAttempt`, `TechnicalValidationWrongComponent`). They are called only by the runner; nothing in the participant flow references them (`grep TechnicalValidation Assets/Scripts --include=*.cs` outside the `TechnicalValidation/` folder lists only those definitions). They raise the same events the validator raises at the end of a real attempt and change nothing until called. |
| Continue | Scripted Continue presses call `AssemblySessionController.AdvanceStepManually()`, the Continue button's own method. |

Nothing in the runner sets a support level, calls the provider, or changes the scheduler, the
gate, the prompt, the context, the physiology interpretation or the logging schema.

## Files

| File | Role |
|---|---|
| `Assets/Scripts/TechnicalValidation/TechnicalValidationBootstrap.cs` | Activation (config file), scenario loading, physiology setup, runner creation |
| `Assets/Scripts/TechnicalValidation/TechnicalValidationRunner.cs` | Injects the script at real-time delays through the production entry points; observes requests, decisions, transitions, steps, presentation; writes the validation JSONL and the summaries |
| `Assets/Scripts/TechnicalValidation/TechnicalValidationOverlay.cs` | The head-locked TECHNICAL VALIDATION readout (validation mode only) |
| `Assets/Scripts/TechnicalValidation/TechnicalValidationConfig.cs` | Config file model, mode defaults, delay scaling (plain C#, tested) |
| `Assets/Scripts/TechnicalValidation/ValidationScenario.cs` | Scenario model and parser; rejects any support-level event (plain C#, tested) |
| `Assets/Scripts/TechnicalValidation/Level2Summary.cs` | Run record, the 16 integration assertions, per-scenario and aggregate summaries (plain C#, tested) |
| `Assets/Scripts/TechnicalValidation/PlacementMotion.cs` | Presentation-only placement staging: gating (presentation only, never validation or participant), ease-in-out, lifecycle, once-only hand-over to the production success path (plain C#, tested) |
| `Assets/Scripts/TechnicalValidation/PresentationPlacementAnimator.cs` | Unity side of the staging: a **renderer-only proxy** of the armed part is highlighted and flown to the production snap pose while the real part is hidden and untouched; then the production `Succeed()` path runs on the real part exactly as in validation mode (the real part is never moved by the animator: the production validator would accept it early) |
| `Assets/Scripts/TechnicalValidation/CompletionSequencer.cs` | End-of-run ordering: the event whose injection completes the sequence is committed before `technical_validation_completed`, which is written once with the final `events_injected` (plain C#, tested) |
| `Assets/Resources/TechnicalValidation/TV1..TV6.json` | The six bundled scenarios (generated by `Tools/TechnicalValidation/generate_scenarios.py`) |
| `Tools/TechnicalValidation/tv.ps1` | adb helper: push config, launch, pull logs and recordings |
| `Tools/PhysiologyTests/TechnicalValidationTests.cs` | Headless tests 40a-40l |

## Configuration file

`<persistentDataPath>/AdaptiveAR/config/technical_validation.json` (on the Quest:
`/sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/config/technical_validation.json`):

```json
{
  "enabled": true,
  "scenario": "TV2",
  "mode": "validation",
  "physiology": "nominal",
  "participantId": null,
  "delayScale": null,
  "minDelaySeconds": null,
  "postTransitionHoldSeconds": null,
  "finishDelaySeconds": null,
  "startDelaySeconds": 3,
  "sensorStabilisationSeconds": null,
  "overlay": true,
  "verbose": true
}
```

| Key | Meaning |
|---|---|
| `scenario` | `TV1`..`TV6`, or any `<id>.json` placed in `<persistentDataPath>/AdaptiveAR/technical_validation/scenarios/` (override without a rebuild) |
| `mode` | `validation`: authored delays at 1x real time, the production scheduler timing (4 s call protection, 15 s change hysteresis, 10 s first look) applies untouched. `presentation`: same scenario, delays x0.5 with a 2.5 s floor and a 6 s hold after each applied support transition so the cue, the replayed instruction and the new guidance are visible. Presentation is for recording only, not an experimental condition. |
| `physiology` | `nominal` / `elevated` (synthetic PPI through the production `ReplayPpiSource -> PhysiologicalStateProvider` path; signal profiles only, no state is asserted) or `unavailable` (no provider; the decision context says physiology is missing; the sensor-setup screen is skipped). The BLE receiver never scans in validation mode. |
| `participantId` | Written on every production log line; default `TV_<scenario>`. Condition is always `technical_validation`. |
| `delayScale`, `minDelaySeconds`, `postTransitionHoldSeconds`, `finishDelaySeconds` | Override the mode defaults (`null` = default). |
| `placementMotionSeconds`, `placementHighlightSeconds` | **Presentation only.** A scripted success first highlights a visual proxy of the armed part (scale pulse, default 0.35 s) and flies it with an ease-in-out curve to the pose the production snap will produce (default 0.9 s); the real part stays untouched until the same production success path then snaps it. `0` switches the motion off. Ignored in validation mode: the effective values are always 0 there, so formal results never depend on animation timing. |
| `sensorStabilisationSeconds` | Shortens the onboarding sensor-stabilisation wait for the synthetic stream (production value 60 s). Onboarding UI timing only. |

No clock is faked. Both modes run in real time; the only acceleration is shorter waits between
scripted events, so every production component observes one coherent clock.

## Scenarios

Each scenario is a chronological script over the **real authored step sequence** of the build
(`Step_01_Crankshaft` .. `Step_06_Camshaft` and their action ids). Event kinds:
`continue`, `success`, `failed_attempt`, `error` (counted), `wrong_component`, `wait`, `note`,
`finish_sequence` (clean Continue/success events every `finishDelaySeconds` until the sequence
ends, so the session closes through the production completion path). Support levels cannot
be scripted; the parser rejects any other kind.

| Id | Title | Script (mirrors Level 1) | Descriptive expectation |
|---|---|---|---|
| TV1 | Clean successful assembly | S1: every action correct, ordinary pacing | remain L1, normal step resets |
| TV2 | Initial difficulty then recovery | S2: head-on-mat burst (fail, error, wrong part, error, fail) then clean | L1->L2, maybe L3; recovery observed, no DECREASE prescribed |
| TV3 | Persistent difficulty | S3: errors on the crankshaft and every piston placement | escalation to L3 through the runtime; L3 presentation applied |
| TV4 | Noisy / erratic | S5: alternating outcomes over two piston steps | no oscillation, no invalid state |
| TV5 | Slow but accurate | S6: no errors, long intervals (crank 150 s, piston actions 30-70 s) | elapsed time alone does not escalate |
| TV6 | L3 recovery | S8: two bursts inside the hysteresis, then sustained clean work | reach L3, then the frozen model's recovery behaviour |

Authored duration at 1x: TV1 ~2 min, TV2 ~3 min, TV3 ~4 min, TV4 ~5 min, TV5 ~9 min, TV6 ~3 min,
plus the finish tail (about 20 clean events at 4 s).

## What is observed and asserted

The runner subscribes to the production events (`OnActionEntered`, `OnSupportLevelChanged`,
`OnStepChanged`, `OnSessionComplete`, `Application.logMessageReceived`) and polls
`DecisionScheduler.Requests` and `AssemblySessionController.LastDecisionResponse`. After the
run it reads the production JSONL back. Thirteen structural assertions are evaluated:

1. scenario started at L1
2. all scripted events entered the production ledger (every event injected; the session's `errors_total` and `attempts_total` equal the injected counts)
3. provider requests occurred where structurally appropriate (every step with >= `minEventsPerRequest` error-like events was asked; at least one request)
4. every provider decision (parsed or fallback) was logged (decisions observed == requests == `decision` lines in the JSONL)
5. every applied support transition obeyed the one-level constraint (and boundary clamps are recorded as `SaturatedAtBoundary`)
6. `SupportLevelController` level matches the decision log after each decision
7. displayed support package corresponds to the actual support level (the instruction panel shows the current level's `GuidanceText` for the action, never another level's; ghost state recorded)
8. no impossible support state
9. step entry reset to L1
10. session completed without an uncaught exception
11. production JSONL output closed correctly (last line is `session_end`)
12. scheduler call protection respected (no two requests closer than `minCallIntervalSeconds`)
13. support-change hysteresis respected; no rapid oscillation: no two applied AI transitions closer than the production change cooldown. A direction reversal after the cooldown (e.g. L2→L3 then L3→L2 39 s later) is a legitimate adaptation and is only reported descriptively as `reversals`; `oscillations` in the summary counts reversals inside the cooldown
14. every scripted event acted on its authored action (a scripted event whose authored action is not the production flow's current action is not injected, and the run fails this check)
15. authored actions entered in script order; none skipped (the scripted action ids must be an in-order subsequence of the production `action_entered` ids)
16. placed parts stayed active and visible after every injected success (object state is observed after each injection; a missing observation fails too)

No assertion grades the model's choice. Scenario expectations are descriptive.

## Outputs (on the device, `<persistentDataPath>/AdaptiveAR/`)

| Path | Content |
|---|---|
| `sessions/<participant>_<ts>.jsonl` | The **normal production session JSONL**, unchanged schema, plus `note` records `technical_validation_started:...`, `tv_event:idx=.. kind=.. injected=.. step=.. action=.. level=..` (one per injected event) and `technical_validation_completed:...` for alignment |
| `technical_validation/<scenario>_<session>.tv.jsonl` | Validation JSONL (`adaptivear.technical_validation.v1`): `technical_validation_started`, `injected_event`, `provider_request`, `provider_decision`, `support_transition`, `step_entered`, `action_entered`, `presentation_check`, `presentation_motion_started` / `presentation_motion_completed` / `presentation_motion_skipped` (presentation mode only, diagnostic: duration, distance, `real_part_untouched`, `production_action_unchanged`), `injected_event` diagnostics (`action_before` = authored action, `scripted_action`, `armed_action`, `object`, `object_active_before/after`, `object_visible_after`, `object_parent_after`, `object_world_scale_after`, `next_action`), `technical_validation_completed` (written after the last `injected_event`; `events_injected` / `events_total` are the final counts), all stamped with the same `t_session_ms` as the production log |
| `technical_validation/<scenario>_<session>.summary.md` / `.json` | The compact Level 2 summary with the assertions (format below) |
| `technical_validation/level2_summary.md` | Aggregate over every summary in the folder, regenerated after each run |

### Summary format (example, format only)

```
# Level 2 technical validation: TV2 Initial difficulty then recovery
mode=validation physiology=nominal delay_scale=1 build=1.0/6000.0.57f1/runtime@0b918b1 provider=anthropic:claude-sonnet-5-5 (llm)
start 2026-10-30T10:00:00.000Z  end 2026-10-30T10:04:12.000Z  duration 252 s
scripted events 18 (+21 finish-tail), injected 39 / 39
provider calls 6 | KEEP 4 INCREASE 2 DECREASE 0 | fallbacks 0 | provider errors/timeouts 0 | malformed 0
support trajectory: L1 -(45s ai_decision)-> L2 -(60s ai_decision)-> L3 -(123s step_default)-> L1
maximum level reached: L3   AI transitions 2   reversals 0 (inside cooldown 0)
structural violations: 0
logger output: /storage/emulated/0/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/sessions/TV_TV2_20261030_100000.jsonl
validation log: /storage/emulated/0/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/technical_validation/TV2_TV_TV2_20261030_100000.tv.jsonl
integration assertions: 16 passed / 0 failed
  PASS  scenario started at L1: level at start L1_Minimal, first step entered at L1_Minimal
  PASS  all scripted events entered the production ledger: 39 event(s), 0 not injected, ...
  ...
decisions:
  #0 45.0s step 1 L1 INCREASE_SUPPORT Ok -> L2 1210 ms
  ...
```

## Running a scenario on the Quest

Prerequisites: the frozen APK (commit `0b918b1` or later) installed; the API key file
already on the device (`.../files/AdaptiveAR/config/ai_provider.local.json`); `adb` from the
Unity Android SDK (`C:\Program Files\Unity\Hub\Editor\6000.0.57f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`).
`Tools/TechnicalValidation/tv.ps1` wraps the steps below.

### 1. TV1 in validation mode

```powershell
cd <repo>
.\Tools\TechnicalValidation\tv.ps1 push -Scenario TV1 -Mode validation -Physiology nominal
.\Tools\TechnicalValidation\tv.ps1 launch
```
`push` writes the config above and copies it to `/sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/config/technical_validation.json`;
`launch` force-stops and starts the app. Then, in the headset:

1. The magenta **TECHNICAL VALIDATION** panel appears low in the view with `waiting for assembly start`.
2. Sensor setup: with synthetic physiology the stream is ready at once; the production stabilisation wait runs (60 s unless `sensorStabilisationSeconds` was set). Press **Continue**.
3. Onboarding -> place the workspace (confirm the reticle) -> **Begin Assembly**, exactly as a participant would.
4. The runner starts 3 s after the first step is entered and runs the script. Nothing else is needed; keep the headset on the bench or the head so the panels stay in view.
5. The sequence completes through the production completion screen; the overlay shows `completed` and the assertion count.

### 2. TV2 in presentation mode (for recording)

```powershell
.\Tools\TechnicalValidation\tv.ps1 push -Scenario TV2 -Mode presentation -Physiology nominal -SensorStabilisationSeconds 10
.\Tools\TechnicalValidation\tv.ps1 launch
```
Delays are halved (floor 2.5 s) and the run pauses 6 s after every applied support
transition. Visible in order: the instruction, the transition notice and tone, the replayed
instruction, the richer guidance (ghosts, arrows, panel density), then the next action.

### 3. Recording the Quest view

Simplest, and the only method that captures passthrough: the headset's own recorder.
Press the Meta button, open the camera / Quick Settings capture control and start
**Record Video** before pressing Begin Assembly (or hold the Meta button and press the
right trigger). Recordings land in `/sdcard/Oculus/VideoShots/`. Fallback from the PC
(no passthrough, one eye, lower quality):

```powershell
adb shell screenrecord --size 1024x1024 --bit-rate 8000000 --time-limit 180 /sdcard/tv_TV2.mp4
```
The recording is presentation evidence only; the quantitative record is the JSONL.

### 4. Retrieving the recording and the logs

```powershell
.\Tools\TechnicalValidation\tv.ps1 pull -Out .\eval_out\level2
```
pulls `sessions/`, `technical_validation/` (validation JSONL, summaries, `level2_summary.md`)
and `/sdcard/Oculus/VideoShots/`. Equivalent manual commands:

```powershell
adb pull /sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/sessions          .\eval_out\level2\sessions
adb pull /sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/technical_validation .\eval_out\level2\technical_validation
adb pull /sdcard/Oculus/VideoShots                                                                   .\eval_out\level2\video
```
Align a recording with the logs by the `technical_validation_started` note (`t_utc`) and
the `tv_event` notes (`t_session_ms`) in the production JSONL.

### 5. Returning the device to participant mode

```powershell
.\Tools\TechnicalValidation\tv.ps1 off
```
removes the config file. The next launch logs `[TechnicalValidation] off` and behaves as a
participant session; the participant id returns to the scene value.

## Tests

`dotnet run --project Tools/PhysiologyTests -c Release` includes 40a-40l: config defaults and
overrides, delay scaling, scenario parsing (support-level events rejected), every assertion
on synthetic records (clean record passes 13/13; multi-level, mismatch, missing reset,
unclosed JSONL, missing decision, call-interval and hysteresis breaches, un-injected events,
counter mismatches, boundary clamp, presentation mismatch, exceptions are each detected),
summary writers, and the six bundled scenarios (parse, real action ids, placement outcomes
only on physical actions, `.meta` present).

Device verification status: the runtime and editor assemblies type-check and the headless
tests pass. The runner, overlay, injection and physiology setup are **not yet
device-verified**; the first on-device run is TV1.
