# Decision layer — AI provider, reference policy, scheduler, technical evaluation

Status 2026-10-27: implemented on branch `hrv-integration`; compiles; headless tests pass; the headset path is **[QV]** until a Quest run with a key.

## What decides, and what never does

- The AI is a **selector**: it returns one of `KEEP_SUPPORT`, `INCREASE_SUPPORT`, `DECREASE_SUPPORT`. Anything else is a recorded constraint violation (`Unparseable`, `OutOfVocabulary`, `ProviderError`, `ProviderTimeout`) with the safe fallback `KEEP_SUPPORT`; it is never silently corrected. The existing `AssemblySessionController.ApplyDecision` enforces this and logs every exchange as a `decision` line.
- The only code that changes a support level is still `SupportLevelController.ApplyAction / SetSupportLevel`, called from the session (`ai_decision`), `StepRunner` (`step_default`) and `SupportLevelDebugInput` (`debug_input`). Physiology code references none of these.
- Physiology is context: it appears in the model input as a nullable block (or `null`), and the **reference policy has no physiological rule** by design (CLAUDE.md §2.4).

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
- `schedule`: `onActionEnter`, `onCountedError`, `onWrongComponent` (a wrong part picked up; same gate as every trigger: one request in flight, `minIntervalSeconds`, `firstDecisionAfterStepMs`, so repeated wrong grabs cannot spam the provider), `periodicSeconds` (0 = off), `minIntervalSeconds`, `firstDecisionAfterStepMs`
- `llm`: `provider` (anthropic), `model`, `endpoint`, `apiVersion`, `maxTokens` (16: one bare token), `timeoutMs`, `systemPrompt` (null = built-in). No sampling parameters are sent: `temperature` is rejected by `claude-sonnet-5-5` (HTTP 400, found on the 2026-10-27 Quest run) and the request builder never emits it; a `temperature` key in a config file is ignored.
- `referencePolicy`: `name`, `slowRatioIncrease`, `errorsOnStepIncrease`, `attemptsOnStepIncrease`, `fastRatioDecrease`, `minStepElapsedMsForIncrease`, `minStepElapsedMsForDecrease`, `clampAtBoundaries`, `fallbackExpectedMs` — **placeholders pending the literature review**; the `name` is written into every decision and evaluation output.

**API key** (never in git, never logged):
```
adb push ai_provider.local.json /sdcard/Android/data/com.vuforia.engine.VuforiaEngine/files/AdaptiveAR/config/ai_provider.local.json
```
or copy `ai_provider.local.example.json` → `Assets/Resources/Decision/ai_provider.local.json` (git-ignored; baked into that build). With `provider: llm` and no key the scheduler is OFF, logs an ERROR and the HUD row reads `AI no key`.

## Response contract

The model's whole reply must be one bare token: `KEEP_SUPPORT`, `INCREASE_SUPPORT` or `DECREASE_SUPPORT`. The system prompt and a one-line user-turn instruction both say so; no reason is requested; `maxTokens` is 16. The parser is strict: a bare token or a complete `{"action": ...}` object parses, everything else (prose, truncated JSON, unknown tokens) is a recorded violation with fallback `KEEP_SUPPORT`. History: the 2026-10-28 Quest run asked for `{"action","reason"}` under 64 tokens; every reason was cut mid-sentence, the object never closed, and all five decisions were (correctly) `Unparseable`. The contract changed; the parser did not.

## Physiology in the contract

The system prompt states the role explicitly: task performance evidence is primary; the optional physiological block is corroborating context that may strengthen or weaken a task-supported conclusion but never causes a change by itself; missing, stale or low-quality physiology is absent evidence and never counts against the operator; there is no fixed threshold or stress cut-off. The reference policy (`placeholder_v0`) still has no physiological rule.

## Support transition cue

When an applied Ok decision raises the level (L1→L2 or L2→L3) the session shows "Additional guidance enabled" on the participant card for a few seconds, plays a soft notice tone that does not cut narration, waits briefly, then replays the current action's authored instruction through the normal speech path (repeat guard bypassed) with the new guidance already rendered. Nothing happens for KEEP, downward, saturated, fallback or failed decisions. Logged as `support_transition_cue` (twice: `instruction_replay=pending`, then the outcome) with `from_level`, `to_level`, `action_id`, `message_surface`, `tone`, `decision_index`. Wording, hold time and delay are inspector fields on `AssemblySessionController`.

## Live flow

`DecisionScheduler` (persistent, binds each scene) → on a trigger → `AssemblySessionController.RequestAndApplyDecisionRoutine(provider)` → `BuildDecisionContext()` (task state + physiology snapshot or null) → `LlmDecisionProvider` → `LlmDecisionCore.Interpret` → `ApplyDecision` (constrained set, fallback, `decision` log line with `context`, `ai_input`, `response.raw_output`, `constraint_status`, `latency_ms`, `level_before/after`, `applied`) → `SupportLevelController.ApplyAction("ai_decision")` → presenter / panels re-render the level.

Log lines added: `decision_config` (at bind, no secrets), `decision_trigger` (why a request was made: `action_enter:<id>`, `counted_error`, `wrong_component_grabbed:<part>`, `periodic`), `decision` (existing, now also carries `ai_input`), `decision_context_probe` (full context at every action entry, replayable).

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
- The reference-policy numbers from the literature review (replace `placeholder_v0`).
- Run the full evaluation with the model and keep the `eval_out` folders with the dissertation data.
