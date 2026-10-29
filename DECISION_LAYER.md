# Decision layer — AI provider, reference policy, scheduler, technical evaluation

Status 2026-10-27: implemented on branch `hrv-integration`; compiles; headless tests pass; the headset path is **[QV]** until a Quest run with a key.

## What decides, and what never does

- The AI is a **selector**: it returns one of `KEEP_SUPPORT`, `INCREASE_SUPPORT`, `DECREASE_SUPPORT`. Anything else is a recorded constraint violation (`Unparseable`, `OutOfVocabulary`, `ProviderError`, `ProviderTimeout`) with the safe fallback `KEEP_SUPPORT`; it is never silently corrected. The existing `AssemblySessionController.ApplyDecision` enforces this and logs every exchange as a `decision` line.
- The only code that changes a support level is still `SupportLevelController.ApplyAction / SetSupportLevel`, called from the session (`ai_decision`), `StepRunner` (`step_default`) and `SupportLevelDebugInput` (`debug_input`). Physiology code references none of these.
- Physiology is context: it appears in the model input as a nullable block (or `null`), and the **reference policy has no physiological rule** by design (CLAUDE.md §2.4).
- **The application never classifies the operator.** It records what happened and when (cumulative counters, since-change counters, temporal features, an ordered event history, support history, physiology) and enforces only structural constraints on the output: the valid action set, one level per decision, the L1/L3 boundaries, one request in flight, call protection and change hysteresis (deferral, never substitution). Whether the operator is struggling, recovering or adequately supported is the model's judgement, made over all signals together. The reference policy is a deterministic comparator for the evaluation, not a pre-filter on the live path.

## Files

| File | Role |
|---|---|
| `Assets/Scripts/Decision/MiniJson.cs` | dependency-free JSON (config, model responses, replay) |
| `Assets/Scripts/Decision/ReferencePolicy.cs` | `ReferencePolicyConfig` (JSON, **placeholder values**) + deterministic `ReferencePolicyProvider : IDecisionProvider` |
| `Assets/Scripts/Decision/LlmDecisionCore.cs` | prompt + request building (the exact AI input), strict response parsing — shared by headset and harness |
| `Assets/Scripts/Decision/LlmDecisionProvider.cs` | Unity transport (UnityWebRequest coroutine), `IAsyncDecisionProvider` |
| `Assets/Scripts/Decision/DecisionConfig.cs` | `decision_config.json` model; `SecretStore` (key file reader, fingerprint only) |
| `Assets/Scripts/Decision/DecisionScheduler.cs` | when to request (action entry / counted error / periodic, min interval, min time on step); owns the provider; `DecisionBootstrap` creates it. `ResearcherHud` reads its `Status` / `ProviderDisplayName` / `LastResponse` |
| `Assets/Scripts/Decision/Evaluation/*` | `ScenarioGenerator` (seeded synthetic grid + noise / missing / adversarial perturbations), `ContextJsonl` (replay recorded contexts), `EvaluationRunner` (metrics + outputs) |
| `Tools/DecisionEval/` | headless console runner (HttpClient transport) |
| `Tools/PhysiologyTests/` | headless tests incl. policy rules, parser strictness, generator determinism, harness metrics, replay |
| `Assets/Resources/Decision/decision_config.json` | default configuration (committed, no secrets) |
| `Assets/Resources/Decision/ai_provider.local.example.json` | key file template; the real `ai_provider.local.json` is git-ignored |

## Configuration

`decision_config.json` (override on the headset: `persistentDataPath/AdaptiveAR/config/decision_config.json`):

- `enabled`, `provider`: `llm` | `reference` | `none`
- `schedule`: `onActionEnter`, `onCountedError`, `onWrongComponent` (a wrong part picked up), `onActionComplete` (a validated placement or tool action done). The gate (`RequestGate`, plain C#) keeps **provider-call protection** (`minCallIntervalSeconds` 4, one request in flight) apart from **support-change hysteresis** (`changeCooldownMs` 15000 after an applied change; requests are deferred, never dropped or turned into KEEP). Error-like events accrue: a request fires after `minEventsPerRequest` (2) of them, or `pendingFlushMs` (8000) after a lone one. Success-only evidence asks only when a decrease is structurally available (above L1) and backs off while nothing changes (`successOnlyBackoff`: 2, 4, 8 successes up to `successOnlyBackoffMax`), so a long clean run never produces a KEEP call every few seconds. An action entry with nothing pending gives one first look per step after `firstDecisionAfterStepMs` (10000). `periodicSeconds` (0 = off). The gate never reads the task state beyond "is a decrease structurally available"; the provider sees everything that accrued.
- `llm`: `provider` (anthropic), `model`, `endpoint`, `apiVersion`, `maxTokens` (16: one bare token), `timeoutMs`, `systemPrompt` (null = built-in). No sampling parameters are sent: `temperature` is rejected by `claude-sonnet-5-5` (HTTP 400, found on the 2026-10-27 Quest run) and the request builder never emits it; a `temperature` key in a config file is ignored.
- `referencePolicy`: `name`, `slowRatioIncrease`, `errorsOnStepIncrease`, `attemptsOnStepIncrease`, `fastRatioDecrease`, `minStepElapsedMsForIncrease`, `minStepElapsedMsForDecrease`, `clampAtBoundaries`, `fallbackExpectedMs`, `recoverySuccessesForDecrease`, `recoveryQuietMs`, `minMsBetweenChanges` — **placeholders pending the literature review** (`placeholder_v1`); the `name` is written into every decision and evaluation output.

**API key** (never in git, never logged):
```
adb push ai_provider.local.json /sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/config/ai_provider.local.json
```
or copy `ai_provider.local.example.json` → `Assets/Resources/Decision/ai_provider.local.json` (git-ignored; baked into that build). With `provider: llm` and no key the scheduler is OFF, logs an ERROR and the HUD row reads `AI no key`.

## Response contract

The request disables extended thinking explicitly (`"thinking": {"type": "disabled"}`): the 2026-10-28 live smoke test showed `claude-sonnet-5-5` sometimes opening its reply with a thinking block whose signature alone exhausted the 16-token budget, so 25 of 194 decisions (12.9 %) became Unparseable fallbacks. Prompt, context, contract, parser and budget are unchanged by this.

The model's whole reply must be one bare token: `KEEP_SUPPORT`, `INCREASE_SUPPORT` or `DECREASE_SUPPORT`. The system prompt and a one-line user-turn instruction both say so; no reason is requested; `maxTokens` is 16. The parser is strict: a bare token or a complete `{"action": ...}` object parses, everything else (prose, truncated JSON, unknown tokens) is a recorded violation with fallback `KEEP_SUPPORT`. History: the 2026-10-28 Quest run asked for `{"action","reason"}` under 64 tokens; every reason was cut mid-sentence, the object never closed, and all five decisions were (correctly) `Unparseable`. The contract changed; the parser did not.

## Context v2: evidence, not classification

Beside the cumulative counters (`errors_on_step`, `attempts_on_step`, `step_elapsed_ms` / `step_expected_ms`) the context carries:

- **since the last support change** (or step entry): `errors_since_last_support_change`, `attempts_since_last_support_change`, `successful_actions_since_last_support_change`, `time_since_last_support_change_ms`;
- **temporal features** from the step's event ledger (`TaskEventLedger`): `recent_error_count`, `recent_failed_attempt_count`, `recent_wrong_component_count` inside `recent_window_ms` (config `context.recentWindowMs`, 60 s), `consecutive_successful_actions`, `errors_since_last_success`, `failed_attempts_since_last_success`, `time_since_last_error_ms`, `time_since_last_success_ms`;
- **`recent_events`**: the newest `context.recentEventsMax` (12) events, oldest first, each `{type, age_ms, action_id, level}` with type in `error`, `failed_attempt`, `wrong_component`, `success`, `support_increase`, `support_decrease`, e.g. `error → error → support_increase → error → success → success`;
- **`current_support_episode`**: an objective segmentation of the step's history at the most recent applied support change: `level`, `started_by` (`support_increase` / `support_decrease` / `step_entry`), `duration_ms`, `errors`, `failed_attempts`, `wrong_components`, `successes`, `consecutive_successes`, `time_since_last_error_ms`, `time_since_last_failed_attempt_ms` and `events` (only those under the current level). It lets the model separate the evidence that led to the current level from how the operator has performed WITH it; the 60 s window and the cumulative counters still contain both. Not a classification. Motivation: the 2026-10-28 v2 run stayed at L2 through four clean successes because `recent_error_count` still held the pre-increase errors;
- **`allowed_actions`**: the structurally available actions at the current level (boundaries only);
- the physiological block or `null`.

All of it is in `ai_input`, in every `decision` / `decision_context_probe` line, replayable by the harness and synthesised in the scenario generator. Nothing in it is a verdict.

**The reasoning instruction** (system prompt) tells the model that it is responsible for interpreting whether the operator is struggling, recovering or adequately supported; to consider the complete history to see how the current level was reached; to prioritise recent performance when judging whether the current level still fits; that old errors keep historical value but lose weight after sustained success; that recent repeated failures mean unresolved difficulty; that recent success after earlier difficulty may mean the added support is working and could eventually be reduced; to interpret elapsed time together with the events, never alone; that performance after the most recent support change is the strongest evidence for whether the current level remains necessary, that errors from before it are not failures of the current level, that sustained success under added guidance means asking whether that guidance has done its job and a one-level reduction fits, that "it is working" is not a reason to keep it indefinitely because the system should provide the minimum support currently needed, and that after a reduction monitoring continues and new difficulty raises support again; that adaptation is bidirectional and one level per decision; to avoid oscillation; and that physiology corroborates but never decides (and never alone justifies a decrease). The model's whole reply is still one bare token.

**Logged per decision**: the cumulative, since-change and temporal context, `recent_events`, physiology, `allowed_actions`, the raw and parsed response with its constraint status, `applied_action`, and `constraint_applied` (`null`, `boundary_clamp` or `fallback:<status>`), plus `evidence`: one line from `DecisionEvidence.Summarise` restating cumulative, since-change, recent and event evidence and that physiology is context only. AI-driven `support_change` lines carry the same `evidence`; logcat prints `[Decision] L2_Guided -> L3_Assisted by <provider>; evidence: ...` on every applied change.

**Reference policy (comparator).** `placeholder_v1` keeps its transparent rules for the offline agreement metric: cumulative thresholds until the level changes on a step, then only since-change errors (`errorsSinceChangeIncrease`) or repeated unsuccessful attempts (`attemptsSinceChangeIncrease`) escalate, recovery (`recoverySuccessesForDecrease`, `recoveryQuietMs`) de-escalates one level, `minMsBetweenChanges` guards oscillation, no physiological rule. It is not on the live path and does not pre-classify anything for the model. Tests 17a–k, 18a–j (comparator), 19a–h (ledger), 20a–k (request gate), 21a–f (prompt, constraints, evidence), 22a–h (support episode: resets at each applied change, contains only later events, travels in ai_input / log / replay / scenarios).

## Physiology in the contract

The system prompt states the role explicitly: task performance evidence is primary; the optional physiological block is corroborating context that may strengthen or weaken a task-supported conclusion but never causes a change by itself; missing, stale or low-quality physiology is absent evidence and never counts against the operator; there is no fixed threshold or stress cut-off. The reference policy (`placeholder_v1`) still has no physiological rule.

## Support transition cue

When an applied Ok decision raises the level (L1→L2 or L2→L3) the session shows "Additional guidance enabled" on the participant card for a few seconds, plays a soft notice tone that does not cut narration, waits briefly, then replays the current action's authored instruction through the normal speech path (repeat guard bypassed) with the new guidance already rendered. Nothing happens for KEEP, downward, saturated, fallback or failed decisions. Logged as `support_transition_cue` (twice: `instruction_replay=pending`, then the outcome) with `from_level`, `to_level`, `action_id`, `message_surface`, `tone`, `decision_index`. Wording, hold time and delay are inspector fields on `AssemblySessionController`.

## Live flow

`DecisionScheduler` (persistent, binds each scene) → on a trigger → `AssemblySessionController.RequestAndApplyDecisionRoutine(provider)` → `BuildDecisionContext()` (task state + physiology snapshot or null) → `LlmDecisionProvider` → `LlmDecisionCore.Interpret` → `ApplyDecision` (constrained set, fallback, `decision` log line with `context`, `ai_input`, `response.raw_output`, `constraint_status`, `latency_ms`, `level_before/after`, `applied`) → `SupportLevelController.ApplyAction("ai_decision")` → presenter / panels re-render the level.

Log lines added: `decision_config` (at bind, no secrets), `decision_trigger` (why a request was made: `<trigger>+<n>ev(<errors>err,<successes>ok)` with `pending_events`, `pending_errors`, `pending_successes` and `deferred_from` when a protection had held it), `decision` (existing, now also carries `ai_input`), `decision_context_probe` (full context at every action entry, replayable).

## Virtual-participant simulation (Tools/DecisionEval, mode=simulate)

A session-level simulation drives the SAME decision architecture the frozen app runs, on a fake clock, from scripted chronological event streams (`Tools/DecisionEval/Scenarios/S1..S8*.json`, regenerated by `generate_scenarios.py`): step entries, action entries, failed attempts, counted errors, wrong-component grabs, validated successes and clock advances. Support changes are never scripted; the level moves only through `RequestGate` → provider → `DecisionConstraints`. Shared production code is used as-is: `TaskEventLedger`, `RequestGate`, `DecisionConstraints`/`SupportLevels`, `LlmDecisionCore` (prompt, request body, strict parser), `ReferencePolicyProvider`, `DecisionEvidence`, `DecisionConfig`. The Unity-only parts (session counters and `BuildDecisionContext`, `ApplyDecision`, `SupportLevelController.ApplyAction`, `StepRunner` step-default reset, the scheduler's `Update` loop) are mirrored in `Simulation/VirtualSession.cs`, each mirror citing its production source; the live asynchronous ordering is kept (a request fired by a counted error is built before the error is counted, and its answer is applied after the handler, so the triggering error precedes any change in the ledger).

Providers: `live-llm` (the app's Anthropic model, token usage read for the cost estimate), `reference` (the deterministic comparator; never ground truth), `canned[:keep|episode|malformed|sequence:A,B]` (harness-only, for scheduler/recording tests). Physiology variants `null | nominal | elevated | stale` run the identical task stream. Outputs under `eval_out/<timestamp>/`: `raw_decisions.jsonl`, `scenario_runs.csv`, `decision_summary.csv`, `trajectory_summary.csv`, `latency_summary.csv`, `physiology_pairs.csv`, `reference_comparison.csv`, `replay_results.csv` (real headset decision contexts resubmitted), `metamorphic_pairs.csv`, `trajectories.txt`, `evaluation_summary.md`. Tests 30a–m cover the fake clock, scheduler timing and deferral through the session, scenario parsing, episode reset, physiology injection, trajectory recording, summary calculations (Fleiss' κ, weighted κ, percentiles), the canned provider, replay loading and the metamorphic families.

Two properties of the frozen app the simulation makes visible: (1) after an upward change, a step with the authored four actions offers the model one success-only look at the second clean success and none at the third or fourth (clean-run backoff 2 → 4), so de-escalation within a step can only happen at that look or at a later error-triggered request; (2) every step entry resets the level to L1 (step default), so recovery evidence never carries across steps.

## Technical evaluation (CLAUDE.md §3)

Headless, offline, deterministic replay:

```
dotnet run --project Tools/DecisionEval -c Release -- provider=reference count=40 repeats=3 perturb=true out=eval_out/ref
dotnet run --project Tools/DecisionEval -c Release -- provider=llm keyfile=C:\path\ai_provider.local.json count=40 repeats=3 perturb=true out=eval_out/llm
dotnet run --project Tools/DecisionEval -c Release -- provider=llm keyfile=... scenarios=C:\path\P00_20261027_052826.jsonl repeats=3 perturb=false out=eval_out/replay
```

Outputs per run: `results.jsonl` (every call: scenario, kind, action, status, latency, raw output, reference action, exact `ai_input`), `summary.json`, `summary.md`, `run.json`.

Metrics: **consistency** (same context repeated: share equal to the modal action), **agreement** with the reference policy (all Ok calls; base scenarios only), **robustness** (decision unchanged under `noise` and `missing`; compliance under `adversarial` text and extreme values — adversarial *values* may legitimately change a decision, so compliance is the robustness criterion there), **compliance** (status Ok share + status counts), **latency** (mean / median / p95 / max), **failures** (every non-Ok call with raw output).

Smoke results: reference policy against itself: consistency 100 %, agreement 100 %, compliance 100 %, adversarial compliance 100 %, noise/missing robustness 100 % (missing inputs make the timing rules abstain, which keeps the base decision for in-band cases).

## Not done / next

- A Quest run with the key: `decision_config` → `decision_trigger` → `decision` lines with `provider_id=anthropic:…`, Research Panel row `AI  Claude Sonnet 5.5 | KEEP_SUPPORT | Ok | 800 ms`.
- The reference-policy numbers from the literature review (replace `placeholder_v1`).
- Run the full evaluation with the model and keep the `eval_out` folders with the dissertation data.
