# HRV reconstruction audit — Polar Verity Sense PPI → AdaptiveARApp

Date: 2026-10-26. Status: audit and plan only; no repository code changed.

Sources of evidence, in order of authority:
1. Your logcat lines and explicit statements in the Gemini transcript ("Unity Quest 3 BLE Setup.pdf"; page numbers below refer to that PDF).
2. The official Polar BLE SDK source and documentation (polarofficial/polar-ble-sdk on GitHub): `PmdDataFrame.kt`, `PpiData.kt`, `TypeUtils.kt`, `BlePMDClient.kt`, `PmdControlPointCommand.kt`, `PmdMeasurementType.kt`, `PmdControlPointResponse.kt`, `documentation/PPIData.md`, `documentation/TimeSystemExplained.md`, `documentation/SdkModeExplained.md`, `documentation/products/PolarVeritySense.md`.
3. Gemini's explanations and code, treated as hypotheses.

---

## A. Verified from your device logs

| # | Fact | Evidence (transcript) |
|---|---|---|
| A1 | A runtime `BLUETOOTH_CONNECT` request is auto-denied until the permission is declared in the manifest (`CONNECT granted? False`), while the adapter itself reports enabled. | p.4 logcat `[HR] CONNECT granted? False`, `[HR] Bluetooth enabled? True`. |
| A2 | Loose Java files in `Assets/Plugins/Android/` are compiled by Unity's Gradle export; a syntax error surfaces as `:unityLibrary:compileReleaseJavaWithJavac FAILED`. The "Exception while marshalling … package.xml … read-only" lines are noise, not the failure. | p.43–45 build output. |
| A3 | The Quest 3 BLE scanner sees the sensor and connects over LE: `Found Polar: Polar Sense 0A0E0132 - Connecting...` → `Connected! Discovering services...`. Device name as advertised: **"Polar Sense 0A0E0132"** (not "Verity"). | p.25 logcat (19:15:42–43). |
| A4 | The standard Heart Rate Measurement characteristic (0x2A37) on this sensor sends flag byte `00` and a 1-byte HR (`00 3D` = 61 bpm): **no RR field**. | p.14 logcat `Raw Packet: 00 3D`, `Sensor is NOT sending RR flags. Byte[0] was: 0`; p.38 "it is 00". |
| A5 | The sensor exposes the service `fb005c80-02e7-f387-1cad-8acd2d8df0c8` with characteristics `fb005c81-…` and `fb005c82-…`. The assumed `fb000001-0200-…` "PPI service" **does not exist** on this sensor. | p.28 logcat `SERVICE FOUND: fb005c80…`, `CHAR FOUND: fb005c81…`, `CHAR FOUND: fb005c82…`, `POLAR PPI SERVICE NOT FOUND IN LIST!`. |
| A6 | Enabling notifications on `fb005c82` (CCCD write) succeeds and `onDescriptorWrite` fires. | p.48 `Descriptor written. Sending PPI Start command...`. |
| A7 | Writing `01 03` to `fb005c81` produced **no data** in more than a minute, twice (before and after the 2 s delay version). | p.48–50 ("waited over a minute still no pulse"), p.64–66 ("keeps waiting for pulse forever", "still takes too long"). |
| A8 | Writing `02 03` to `fb005c81` produced data: first notification ~16 s after the request (19:59:42 → 19:59:58), then one notification every ~5 s (20:00:03, :08, :13, :18, :23). LED turned pink/purple. | p.52 logcat. |
| A9 | The values Gemini's parser printed from those packets as "PPI" were `0, 0, 14336, 14848, 14592, 15104, 15872` = `0x0000, 0x3800, 0x3A00, 0x3900, 0x3B00, 0x3E00`. Read as little-endian from index 9, that is byte 9 = `0x00` and byte 10 = `0x38…0x3E` = **56–62**, i.e. a plausible resting heart rate in byte 10 and a zero byte at index 9 (frame type 0). This is direct evidence for the official layout in §D (byte 9 = frame type, byte 10 = HR of sample 1). | p.52 logcat. |
| A10 | After the parser moved to index 10 (still a 2-byte stride), it printed "HR: 105 / PPI: 571", "HR: 72 / PPI: 825", "HR: 189 / PPI: 316" with HR derived as 60000/PPI. These "PPI" values are `0x023B`, `0x0339`, `0x013C`: low bytes 59, 57, 60 (the real HR byte), high bytes 2, 3, 1 (the low byte of the real PPI). They are cross-field garbage, not intervals; the sparse output (75 s and 15 s apart) is the `300 < ppi < 2000` filter rejecting most garbage. The "slow sampling" and "RMSSD > 100" complaints (p.61) are therefore parser errors, not sensor behaviour. | p.59, p.61. |
| A11 | Transport used: `UnityPlayer.UnitySendMessage("App", "OnReceiveHeartData", "<STATUS>|…")` with string payloads `CONNECTED|0` and `DATA|<hr>|<ppi>`; one message per parsed sample; the receiving GameObject had to be named exactly `App`. | p.42–47 code that ran, p.58 C# that ran, p.68 Gemini's own description (consistent). |
| A12 | The working build used Unity 2022.3.23f1, min SDK ≥ 31, custom main manifest with `BLUETOOTH_SCAN` (`neverForLocation`) and `BLUETOOTH_CONNECT`, `uses-feature android.hardware.bluetooth_le`. | p.2, p.5. |

Not demonstrated by any log: that a single correctly-decoded PPI value ever reached Unity; the real sample stride; the meaning of the flag byte; anything about RMSSD magnitudes.

---

## B. Likely final architecture (last version that produced data)

Reconstructed from the p.45–47 "Correct BleBridge.java" plus the p.51 change to `02 03` and the p.60 per-sample loop:

- **Scan:** `BluetoothLeScanner.startScan(ScanCallback)` without filters; accept the first `ScanResult` whose `device.getName()` contains "Polar"; `stopScan`; no system pairing (the sensor was unpaired in Quest settings first).
- **GATT:** `device.connectGatt(context, false, callback, BluetoothDevice.TRANSPORT_LE)`; on `STATE_CONNECTED` → `discoverServices()`.
- **Service path:** PMD service `fb005c80-02e7-f387-1cad-8acd2d8df0c8`; control point `fb005c81-…`; data `fb005c82-…`; CCCD `00002902-0000-1000-8000-00805f9b34fb`.
- **Notification setup:** `setCharacteristicNotification(data, true)` + write `ENABLE_NOTIFICATION_VALUE` to the data characteristic's CCCD. The control point's CCCD was never enabled, so command responses were never observed.
- **Stream start:** in `onDescriptorWrite`, write `{0x02, 0x03}` to the control point (the version that produced data). The later `{0x01, 0x03}` + 2 s delay version (p.64) never produced data.
- **Parser (as it ran):** `data[0] == 0x03`, then 16-bit little-endian reads from index 10 with a **2-byte stride** — wrong (see §D1).
- **Batching observed:** one notification every ~5 s after a ~16–25 s warm-up.
- **Unity target:** `UnitySendMessage("App", "OnReceiveHeartData", "DATA|hr|ppi")`, one call per sample, parsed in C# by `string.Split('|')`.
- **Permissions:** `BLUETOOTH_SCAN` (`usesPermissionFlags="neverForLocation"`), `BLUETOOTH_CONNECT`, `uses-feature bluetooth_le`; requested at runtime with `UnityEngine.Android.Permission`. `ACCESS_FINE_LOCATION` was also requested at runtime (p.20, p.57) but is not needed on API 31+ with `neverForLocation`.

---

## C. Dead ends — do not reimplement

1. **Standard Heart Rate service for RR/PPI.** Verity Sense sends flags `0x00`, no RR (A4). Official position: PPI is "extracted from PPG signal" via the PMD service, not the HR profile.
2. **`fb000001-0200-…` / `fb000002-0200-…` "Polar PPI service".** Not present on the sensor (A5).
3. **System pairing then `getBondedDevices()`.** Worked only when the sensor was paired; unpairing was needed to get a clean GATT cache, after which bonded lookup found nothing (p.21). Scan directly.
4. **`01 03` as the start command.** `0x01` is `GET_MEASUREMENT_SETTINGS` in the official SDK, not start (§D2). Both "waits forever" episodes (A7) used it.
5. **Reading only the first PPI of a packet; 2-byte stride; `getShort(9)`; "PPI at index 10".** All wrong (A9, A10, §D1).
6. **Deriving HR as `60000 / ppi` in Java.** The sample carries the device's own HR byte; recompute HR only offline from accepted intervals if wanted.
7. **The 20 % successive-difference filter, the `350–1500 ms` / `333–1500 ms` hard range as a silent drop, and `ppiBuffer.RemoveAt(0)`-only retention.** Silent deletion of samples is not acceptable for the dissertation data (rule: record the original).
8. **Treating "RMSSD 20–100 ms is normal" or "low RMSSD = stressed" as facts; "medical-grade HRV"; "purple LED = SDK mode".** None is supported by Polar documentation; PPI is explicitly unavailable in SDK mode (§D5).
9. **Any threshold-triggered adaptation** (`RMSSD < x → INCREASE_SUPPORT`). Prohibited by CLAUDE.md §2.4 and by this plan.
10. **`UnitySendMessage` to a GameObject named "App"** as the integration point in the real app: the name is a global, collision-prone contract. Keep the mechanism (it is the simplest reliable Java→C# path), but use a unique, dedicated object name.
11. **Logging the whole burst through `Debug.Log` only.** Nothing was persisted; all evidence lived in logcat.

---

## D. Claims that required external verification — official Polar facts

### D1. PPI packet format (corrects the transcript)

From `PmdDataFrame.kt`, `PpiData.kt`, `TypeUtils.kt` (all little-endian):

```
byte 0        measurement type (PPI = 0x03)
bytes 1–8     u64 LE timestamp, nanoseconds since 2000-01-01T00:00:00Z (device clock); may be 0 for PPI
byte 9        frame type; low 7 bits = type (PPI uses TYPE_0 = 0x00); bit 7 = delta/compressed frame (not used by PPI)
bytes 10…     N samples × 6 bytes, N = (length − 10) / 6:
   +0  u8      hr (bpm, device-computed; updated only every 5 s while PPI runs)
   +1  u16 LE  ppi (ms)
   +3  u16 LE  errorEstimate (ms)
   +5  u8      flags: bit0 = blocker (1 = movement detected during acquisition),
                      bit1 = skin contact (valid only if bit2 = 1),
                      bit2 = skin contact supported
```

`PpiData.kt` rejects compressed frames and any frame type other than 0, and requires the payload to be a non-zero multiple of 6. When the frame timestamp is non-zero, the SDK assigns it to the **last** sample and back-fills earlier samples by subtracting each `ppi` (in ns).

Your A9 values (`0x3800…0x3E00` from `getShort(9)`) are exactly byte 9 = 0x00 (frame type 0) and byte 10 = 56–62 (HR), which independently confirms this layout for your sensor.

### D2. Control point protocol (`PmdControlPointCommand.kt`, `BlePMDClient.kt`, `PmdControlPointResponse.kt`)

- Opcodes client→service: `0x01 GET_MEASUREMENT_SETTINGS`, **`0x02 REQUEST_MEASUREMENT_START`**, `0x03 STOP_MEASUREMENT`, `0x04 GET_SDK_MODE_MEASUREMENT_SETTINGS`, `0x05 GET_MEASUREMENT_STATUS`, `0x06 GET_SDK_MODE_STATUS`, …
- Measurement type ids: ECG 0, PPG 1, ACC 2, **PPI 3**, GYRO 5, MAG 6, SKIN_TEMP 7, SDK_MODE 9, …
- Start PPI = `[0x02, 0x03]` (PPI takes no settings bytes). Stop PPI = `[0x03, 0x03]`.
- The SDK enables notifications on **both** the control point and the data characteristic before sending any command (`clientReady` waits for both). The transcript never enabled the control point's notifications, so start-command success/failure was never observed.
- Control point response (notification on `fb005c81`): `[0xF0, opcode, measurementType, status, more, params…]`; status `0 = SUCCESS`, `1 INVALID_OP_CODE`, `2 INVALID_MEASUREMENT_TYPE`, `3 NOT_SUPPORTED`, `4 INVALID_LENGTH`, `5 INVALID_PARAMETER`, `6 ALREADY_IN_STATE`, `12 INVALID_STATE`, `13 DEVICE_IN_CHARGER`, … Expected success reply to start: `F0 02 03 00 00`.
- Service→client opcode `0x01 ONLINE_MEASUREMENT_STOPPED` (followed by type ids) tells the client the stream ended.
- `GET_MEASUREMENT_STATUS` (0x05) and reading the control point characteristic (feature bytes: data[1] bit 0x08 = PPI supported) are available for diagnostics.

### D3. Verity Sense specifics (`PolarVeritySense.md`)

- PPI online streaming and offline recording are supported ("PP interval (milliseconds) representing cardiac pulse-to-pulse interval extracted from PPG signal").
- "When PPI recording is enabled, HR is only updated every 5 seconds."
- "it takes around 25 seconds for the first sample batch to be sent … over BLE for streaming." (Your A8 measured ~16 s.)
- "PPI online stream or offline recording is **not** supported in SDK MODE." The PPI algorithm is "a separate algorithm than the HR one used when PPI data is not being requested."
- "Skin contact of PPI packets should not be trusted." The device may output a non-zero HR while not worn.
- Enabling PPI aborts any ongoing training/swimming session on the sensor.

### D4. PPI semantics and validity (`PPIData.md`)

- PPI is "the signal between two main peaks of the PPG signal", "similar to R-R intervals from ECG sensors, but not in the same phase."
- "Blocker flag is set to 1 if there was movement detected during the acquisition."
- "If skin contact flag is 0 or blocker flag is 1, the sample should not be treated as valid and discarded." (For Verity Sense the skin-contact flag itself is unreliable, D3 — so blocker is the usable validity bit; skin contact is logged, not enforced.)
- PPI "cannot be measured accurately during activity" and "should only be used at complete rest"; applications should ask users not to move. For an assembly task this is a known, documented limitation: movement-heavy periods must be identifiable in the data and treated explicitly in analysis.
- `errorEstimate` (u16, ms) is present in every sample (`PpiData.kt`). The official docs page does not define it; the SDK's own `PolarPpiData` KDoc (not retrievable via fetch at audit time) describes it as the expected absolute error of the PP interval in ms. Treat it as a per-sample quality covariate to log and analyse offline; do not threshold on it in the app without a documented rationale.

### D5. SDK mode (`SdkModeExplained.md`)

SDK mode disables all on-device algorithms; computed data including "PP intervals" is "not available anymore" in SDK mode. The transcript's "purple LED = SDK mode" explanation is therefore not what enables PPI, and SDK mode must **not** be requested.

### D6. Timestamps (`TimeSystemExplained.md`)

Sample timestamps are "nanoseconds since 2000-01-01T00:00:00Z UTC" from the **sensor's own clock**; for PPI the frame time "may be zero or missing"; the sensor clock is a firmware default unless set via `setLocalTime`. Consequence: Unity receipt time (session-relative) is the primary clock for alignment with steps and support changes; the device timestamp is logged when non-zero and only used offline.

### D7. MTU (from `BlePMDClient`/Polar SDK practice; verify on device)

With the default ATT MTU (23 → 20-byte payload) a notification can carry only one 6-byte sample after the 10-byte header. The Polar SDK requests a larger MTU on connect. Stage 3 must call `gatt.requestMtu(512)` after service discovery and log the granted MTU and every notification length; sample count per packet must be derived from length, never assumed.

### D8. Claims with no support anywhere

"medical-grade HRV"; "RMSSD normal range 20–100 ms"; "lower RMSSD indicates stress"; "0xFFFF is a Polar null filler"; "Verity Sense hides PPI unless a known app asks"; "the Quest Bluetooth controller batches too aggressively"; "a 2 s delay is the secret sauce". None of these are used in the plan.

---

## E. Current repository integration points (exact)

| Item | Location | State |
|---|---|---|
| `PhysiologicalSample` | `Assets/Scripts/Decision/DecisionTypes.cs` lines 65–93 | Fields: `float? RmssdMs`, `float? HeartRateBpm`, `float? MeanIbiMs`, `string Source`, `float? Quality`, `float? AgeMs`, `bool IsEmpty`. No timestamp, no sample counts. |
| `DecisionContext` | same file, lines 100–139 | `PhysiologicalSample Physiological` + `PhysiologicalMissing` (null or empty). |
| `IDecisionProvider`, `DecisionResponse`, `ConstraintStatus` | same file | Interface only. **No implementation exists in the repo.** |
| Context builder | `AssemblySessionController.BuildDecisionContext()` line 1296 | Sets `Physiological = LatestPhysiological` (no staleness check). |
| Decision application | `ApplyDecision` line 1336; `RequestAndApplyDecision(IDecisionProvider)` line 1372 | Constrained action set enforced; fallback KEEP_SUPPORT; logged via `LogDecision`. **`RequestAndApplyDecision` has no callers anywhere.** |
| Physiology intake | `AssemblySessionController.LatestPhysiological` (line 385), `SubmitPhysiological(sample)` line 1413 → `logger.LogPhysiological` | Exists, unused (nothing calls `SubmitPhysiological`). |
| Support-level mutation | `SupportLevelController.ApplyAction / SetSupportLevel` | Only callers: session `ApplyDecision` ("ai_decision"), `StepRunner` ("step_default"), `SupportLevelDebugInput` ("debug_input"). No physiology path. |
| Logger | `Assets/Scripts/Logging/SessionLogger.cs` | JSONL at `persistentDataPath/<subFolder>/<participant>_<utc>.jsonl`; schema `adaptivear.session.v1`; envelope: schema, seq, event, t_utc, t_unix_ms, t_session_ms, participant_id, session_id, condition, step_index, step_id, support_level(_num), step_elapsed_ms, attempts_on_step, errors_on_step, errors_total. Events include `physiological` (lines 318–324, writes rmssd_ms/heart_rate_bpm/mean_ibi_ms/quality/age_ms/source), `decision` (writes the context's physiological block and `physiological_missing`), `component_grabbed`/`component_released` (movement proxy), `support_change`, `action_enter/complete`, `step_enter/complete`. Lines written before `BeginSession` are buffered (`_pendingBeforeOpen`) and flushed in order. `JsonLineWriter` has no array support (objects and scalars only). |
| Session start | `AssemblySessionController.Start()` → `BeginSession()` when `beginSessionOnStart`; `AppFlowController.BeginSession()` (home panel Start button) → `StepManager.StartSessionExternal()`; Restart = `SceneManager.LoadScene` (whole scene reloads, logger closed with reason "restart"). | No `DontDestroyOnLoad` objects except runtime helpers (FeedbackPulse, HapticFeedback). |
| Researcher readout | `Assets/Scripts/UI/ResearcherHud.cs` lines 180–192 | Shows "HRV no source" or RMSSD/HR from `session.LatestPhysiological`. Not in the live scene (0 references). `StatusHud` is the live readout. |
| Android manifest | `Assets/Plugins/Android/AndroidManifest.xml` (custom main manifest enabled: `useCustomMainManifest: 1`) | Permissions: HAND_TRACKING, USE_ANCHOR_API, USE_SCENE, HEADSET_CAMERA, VIBRATE. **No Bluetooth permissions, no `bluetooth_le` feature.** No `.java`/`.aar` plugins. Min/target SDK 32, Unity 6000.0.57f1, IL2CPP/ARM64. |
| Scene host | `StepManager` GameObject holds StepManager, SupportLevelController, GuidanceRegistry, StepRunner, StepPresenter, SupportLevelDebugInput, SessionLogger, StepValidator, AssemblySessionController, GuidanceArrow, WorkflowState. | No object named `App`; nothing BLE-related. |

---

## F. Proposed architecture

### F1. Files to create

| File | Role |
|---|---|
| `Assets/Plugins/Android/BleBridge.java` (package `com.adaptivear.ble`) | Android 12+ scan (name filter "Polar", stop on first match), `connectGatt(TRANSPORT_LE)`, `requestMtu(512)`, enable notifications on **both** `fb005c81` and `fb005c82`, write `[0x02,0x03]`, parse control-point responses, unpack every 6-byte sample from every data notification, forward each sample. Also `stopStream()` (`[0x03,0x03]`), `disconnect()`, reconnect-on-drop with back-off, and a state machine reported to Unity. No statistics. |
| `Assets/Scripts/Physiology/PpiSample.cs` | Plain C# record: receipt time (session ms + unity realtime), device timestamp ns (nullable), ppi ms, device hr, error estimate ms, blocker, skin contact, skin contact supported, packet seq, index in packet, samples in packet. |
| `Assets/Scripts/Physiology/PolarPpiReceiver.cs` | MonoBehaviour on a dedicated GameObject named `PolarPpiReceiver` (the `UnitySendMessage` target). Starts the bridge after permissions, parses messages, stamps receipt time immediately, raises `OnSample(PpiSample)` and `OnConnectionState(state, detail)`. No adaptation logic. `DontDestroyOnLoad` so the BLE link survives Restart; re-binds to the scene's provider/logger after each scene load. |
| `Assets/Scripts/Physiology/HrvMath.cs` | Plain C#: RMSSD, mean IBI, HR from intervals; headless-testable. |
| `Assets/Scripts/Physiology/PhysiologicalStateProvider.cs` | MonoBehaviour (plain-C# core inside): keeps raw samples, applies the explicit live acceptance rules, maintains the rolling window, produces the live `PhysiologicalSample`, exposes `Snapshot(maxAgeMs)` (null when stale/insufficient), `Quality`, counts, and emits window summaries to the logger at a cadence and on request. Never touches `SupportLevelController`. |
| `Assets/Scripts/Physiology/IPhysiologicalSource.cs` + `ReplayPpiSource.cs` | Source abstraction (CLAUDE.md §7.4): the Polar receiver and a replay/mock source (from a `ppi_sample` JSONL or synthetic) feed the same provider for bench and offline tests. |
| `Assets/Plugins/Android/BleBridge.java.meta` etc. | Standard metas. |

### F2. Existing files needing minimal edits

| File | Edit |
|---|---|
| `Assets/Plugins/Android/AndroidManifest.xml` | Add `BLUETOOTH_SCAN` (`android:usesPermissionFlags="neverForLocation"`), `BLUETOOTH_CONNECT`, `<uses-feature android:name="android.hardware.bluetooth_le" android:required="false"/>` (not required, so a build without a sensor still launches). No legacy `BLUETOOTH`/`BLUETOOTH_ADMIN`/location (min SDK 32). This is a §5.9 "explain first" change; the HEADSET_CAMERA/passthrough entries stay untouched. |
| `Assets/Scripts/Logging/SessionLogger.cs` | Add events `ppi_sample`, `physio_window`, `physio_connection` and writers; bump `SchemaVersion` to `adaptivear.session.v2` (additive; old files remain interpretable). Optionally extend `WritePhysiological` with `n_accepted`, `window_ms` if `PhysiologicalSample` gains them (see F3). |
| `Assets/Scripts/Decision/DecisionTypes.cs` | Optional, additive: `int? SampleCount`, `float? WindowMs`, `string QualityNote` on `PhysiologicalSample`. Not required for Stage 8. |
| `Assets/Scripts/Steps/AssemblySessionController.cs` | `BuildDecisionContext`: take `Physiological` from the provider's `Snapshot(maxAgeMs)` (null if stale) instead of the raw `LatestPhysiological`; keep `SubmitPhysiological` as the cadence hook that logs `physiological`. Two lines plus a serialized `maxPhysiologicalAgeMs` field. |
| `Assets/Scripts/UI/StatusHud.cs` | One row: HRV source state / RMSSD / sample age (on-device readout required by CLAUDE.md §4.3). |
| Scene `1 - ArUcoMarkerTracking.unity` | One new GameObject `PolarPpiReceiver` with `PolarPpiReceiver` + `PhysiologicalStateProvider` (+ optional `ReplayPpiSource`). Scene YAML edit: explain-first per §5.9. |

### F3. Live acceptance rules (explicit, configurable, all logged)

A raw sample is **accepted for the live window** iff:
1. `blocker == 0` (official validity bit);
2. `ppi` within a configurable plausibility range (default 300–2000 ms; recorded in the session_start line);
3. skin-contact rejection is **off by default** (official: untrusted on Verity Sense) but available as a switch; the flags are logged regardless.

No successive-difference ("20 %") filter online. Every raw sample is logged with `accepted` and `reject_reason` before any decision about it. Window: rolling, time-based (default 60 s, configurable), minimum accepted count (default 10) before RMSSD is reported; otherwise the snapshot is null and `PhysiologicalMissing` is true. `Quality = accepted / raw` in the window; `AgeMs = now − receipt time of the newest accepted sample`.

### F4. Decision integration

At each `BuildDecisionContext()`: `Physiological = provider != null ? provider.Snapshot(maxPhysiologicalAgeMs) : null`. Nothing waits for physiology; a stale or insufficient window yields null. The reference policy and any future provider receive it as context only. No code path from the provider to `SupportLevelController` exists or will be added.

---

## G. Logging schemas

Envelope fields are the existing ones (schema, seq, event, t_utc, t_unix_ms, t_session_ms, participant_id, session_id, condition, step_index, step_id, support_level, support_level_num, step_elapsed_ms, attempts_on_step, errors_on_step, errors_total). Units are in the field names.

### G1. `ppi_sample` (one line per raw sample, never filtered)

| field | type | meaning |
|---|---|---|
| `t_receipt_session_ms` | float | Unity receipt time, session clock (same base as `t_session_ms`) |
| `t_receipt_realtime_s` | float | `Time.realtimeSinceStartup` at receipt |
| `device_ts_ns` | int or null | PMD frame timestamp (ns since 2000-01-01Z), back-filled per sample as the SDK does; null when the frame carries 0 |
| `ppi_ms` | int | raw PP interval |
| `hr_bpm_device` | int | device HR byte (updates every 5 s) |
| `error_estimate_ms` | int | raw |
| `blocker` | bool | movement during acquisition |
| `skin_contact` | bool | raw flag |
| `skin_contact_supported` | bool | raw flag |
| `packet_seq` | int | notification counter since connect |
| `sample_index` / `samples_in_packet` | int | position in the burst |
| `packet_len` | int | notification length (MTU evidence) |
| `accepted` | bool | admitted to the live window |
| `reject_reason` | string or null | `blocker` / `range` / `skin_contact` / `duplicate` |
| `source` | string | `PolarVeritySense/<deviceName>` or `replay/<file>` |

### G2. `physio_window` (every `windowLogIntervalMs`, default 5000 ms, and once inside every `decision` via the context block)

| field | type |
|---|---|
| `window_kind` | `rolling` / `decision` |
| `window_start_session_ms`, `window_end_session_ms` | float |
| `rmssd_ms` | float or null |
| `mean_ibi_ms` | float or null |
| `hr_bpm_from_ibi` | float or null |
| `hr_bpm_device_last` | int or null |
| `n_raw`, `n_accepted`, `n_rejected` | int |
| `n_rejected_blocker`, `n_rejected_range`, `n_rejected_skin` | int |
| `mean_error_estimate_ms` | float or null |
| `completeness` | float (accepted-interval time / window length) |
| `quality` | float (n_accepted / n_raw) |
| `sample_age_ms` | float or null |
| `grab_overlap_ms` | float (time within `component_grabbed`→`component_released` intervals; derived from the session's own grab events) |
| `connection_state` | string |
| `source` | string |
| current step / action / support level | from envelope + `action_id` |

### G3. `physio_connection`

`state` ∈ {permission_requested, permission_denied, scanning, found, connecting, connected, services_discovered, mtu, notifications_enabled, stream_requested, stream_started (with response bytes), stream_stopped, data_first, disconnected, error}, `detail` string, `device_name`, `device_address`.

Session_start line gains: `physio_source`, `physio_accept_rules` (range, skin-contact switch, window ms, min accepted), `physio_bridge_version`.

---

## H. Data flow

```
Polar Verity Sense (PMD service fb005c80)
  │ BLE notifications on fb005c82: [03][ts u64][00][6-byte samples…]
  ▼
BleBridge.java  (scan → connect → MTU 512 → CCCD on 5c81 and 5c82 → write 02 03)
  │ for each 6-byte sample: UnitySendMessage("PolarPpiReceiver","OnPpi",
  │   "S|<pktSeq>|<idx>|<n>|<len>|<ts_ns>|<hr>|<ppi>|<err>|<flags>")
  │ state:       UnitySendMessage("PolarPpiReceiver","OnBleState","<state>|<detail>")
  ▼
PolarPpiReceiver.cs  (stamps receipt time, parses, raises OnSample / OnConnectionState)
  ├──► SessionLogger.LogPpiSample(...)                 ──► JSONL  ppi_sample (every raw sample)
  ▼
PhysiologicalStateProvider.cs  (rules F3, rolling window, HrvMath)
  ├──► SessionLogger.LogPhysioWindow(...)              ──► JSONL  physio_window (cadence)
  ├──► session.SubmitPhysiological(latest)             ──► JSONL  physiological (existing event)
  └──► Snapshot(maxAgeMs) ──► AssemblySessionController.BuildDecisionContext()
                                   └─► DecisionContext.Physiological (null if stale)
                                         └─► IDecisionProvider (context only)
                                               └─► ApplyDecision → constrained action → SupportLevelController
                                                     (the ONLY path that changes support; logged as `decision`)
```

Offline: `ppi_sample` lines allow HRV recomputation with any artifact policy; `physio_window` lines are the live view the app had; `decision` lines record exactly what the provider saw; `component_grabbed/released`, `action_enter/complete`, `support_change` give alignment.

---

## I. Staged implementation plan

Each stage ends with a device log line that proves it; nothing advances on compile alone (CLAUDE.md §4.2).

1. **Permissions and plugin compile.** Manifest additions; `BleBridge.java` skeleton (init/scan only); receiver requests `BLUETOOTH_SCAN` + `BLUETOOTH_CONNECT` at runtime. Proof: Quest permission dialog appears; `physio_connection state=permission_granted`; Gradle `compileReleaseJavaWithJavac` passes.
2. **Discovery.** Scan with name filter; log `found device_name=Polar Sense <id> rssi=`. Sensor unpaired from Quest settings (documented in the researcher checklist).
3. **GATT + stream start.** Connect (LE), `requestMtu(512)` and log granted MTU, enable notifications on control point and data, write `02 03`, log the control-point reply bytes; expect `F0 02 03 00 00`; handle `ERROR_ALREADY_IN_STATE` (6) by stop then start; log `ONLINE_MEASUREMENT_STOPPED`. Proof: `stream_started response=F0020300…`, then `data_first` within ~25 s while worn.
4. **Correct samples in Unity.** Parse per §D1 with 6-byte stride; log `packet_len` and `samples_in_packet`; proof: `ppi_sample` lines with `ppi_ms` in the hundreds, `hr_bpm_device` ≈ 50–100 at rest, blocker mostly 0 at rest and 1 while deliberately moving.
5. **Raw logging.** Every sample persisted to JSONL (buffered before session start); `adb pull` and count lines = samples.
6. **Live summary.** Provider window + `HrvMath` (unit-tested headlessly with a synthetic series of known RMSSD); `physio_window` lines every 5 s; StatusHud row.
7. **`PhysiologicalSample` populated.** `SubmitPhysiological` called at window cadence; `physiological` events present with `source`, `quality`, `age_ms`.
8. **Decision context.** `BuildDecisionContext` uses `Snapshot(maxAgeMs)`; a dry-run decision (existing deterministic fallback path, no provider) logs a `decision` line whose `context.physiological` is non-null when fresh and null when the sensor is removed.
9. **Alignment.** Verify `physio_window` carries step/action/support and `grab_overlap_ms`; cross-check against `action_enter`, `support_change`, `component_grabbed` timestamps in one pulled file.
10. **Independence check.** Static: the only callers of `SupportLevelController.ApplyAction/SetSupportLevel` remain session (`ai_decision`), `StepRunner` (`step_default`), `SupportLevelDebugInput` (`debug_input`). Runtime: with the sensor streaming and no provider, a full run shows no `support_change` whose `source` is anything but those three; `decision` lines show physiology as input only.

Items that remain [QV] until a Quest run: MTU behaviour (D7), first-sample latency, whether the control-point reply arrives as notification or indication on this firmware, and reconnect behaviour.


---

## J. Implementation status (Stages 1–6), 2026-10-26 — branch `hrv-integration`

Original audit (sections A–I) preserved above. All items below are **[QV]**: compiled and unit-tested, not yet run on a Quest 3 with a sensor.

| Stage | State | What exists |
|---|---|---|
| 1 Permissions + plugin compile | implemented | Manifest: `BLUETOOTH_SCAN` (`neverForLocation`), `BLUETOOTH_CONNECT`, `bluetooth_le` feature (not required). `BleBridge.java` compiles against android-32 + Unity classes (javac, 0 errors). Receiver requests both permissions at runtime via `PermissionCallbacks`. |
| 2 Discovery | implemented | `BluetoothLeScanner` low-latency scan, name-substring filter (`Polar`), 30 s scan timeout → rescan with back-off. |
| 3 GATT + stream start | implemented | `connectGatt(TRANSPORT_LE)` → `discoverServices` → `requestMtu(512)` (requested/granted logged) → CCCD on control point → CCCD on data → write `02 03`. `stream_started` only on control-point response `F0 02 03 00 …`; `06` (already in state) → `03 03` then `02 03`; other status → `stream_error`. `ONLINE_MEASUREMENT_STOPPED` → `stream_stopped`. |
| 4 Correct samples in Unity | implemented | Frame validation: length ≥ 10, type 0x03, frame type 0, not compressed, payload multiple of 6; else `malformed_packet` with hex. N = (len − 10)/6. Per sample: hr u8, ppi u16 LE, errorEstimate u16 LE, flags bits 0/1/2. `UnitySendMessage("PolarPpiReceiver","OnPpi","S|seq|idx|n|len|tsNs|hr|ppi|err|flags")`; receipt time stamped first thing in `OnPpi`. |
| 5 Raw logging | implemented | `SessionLogger.LogPpiSample` writes every parsed sample (`ppi_sample`, schema v2) with the live-window decision on it; lines before `BeginSession` are buffered and flushed in order. No MAC address anywhere. |
| 6 Live summary | implemented | `PhysiologyWindow` (plain C#): rolling 60 s, min 10 accepted, range 300–2000 ms, blocker rejection, skin-contact rejection off by default, no successive-difference filter; `physio_window` every 5 s with counts, quality, completeness, age, `grab_overlap_ms`; StatusHud row "PHYSIOLOGY (BLE / HR / PPI / RMSSD)". 20/20 headless tests pass (`dotnet run --project Tools/PhysiologyTests`). |
| 7–10 | **not implemented** (by instruction) | `PhysiologicalSample`, `SubmitPhysiological`, `BuildDecisionContext`, decision seam untouched. Static check: no reference to `ApplyAction`, `SetSupportLevel`, `SupportAction`, `SupportLevel`, decision types anywhere under `Assets/Scripts/Physiology` or in the bridge. |

Scene wiring: no scene edit. `PhysiologyBootstrap` creates the single `PolarPpiReceiver` object at start-up from `Assets/Resources/Physiology/PhysiologyConfig.asset` (enabled, source PolarVeritySense/Replay/None, window settings). Reason: the live scene file carries uncommitted researcher edits that must not be touched, and the object must exist exactly once across Restart (`DontDestroyOnLoad` + duplicate guard; rebinding logged as `[Physio] rebound …` and `physio_connection state=rebind`).

Known limits to confirm on device: control-point response delivery (indicate vs notify — the bridge picks indicate only if the characteristic lacks notify), granted MTU and samples per notification, first-sample latency, reconnect behaviour.
