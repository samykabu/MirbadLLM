# Tuning redesign — implemented

Where each item of the tuning redesign (the review findings and plans v2 and v3, kept in the earlier private history) lives in the code.
Phases shipped as PR #11 (A), PR #12 (C) and `feature/tuning-bd-search` (B + D).

## A. Correctness

| Plan item | Code | Tests |
|---|---|---|
| A1/R2-1 canonical effective config, ExtraArgs lifting (all spellings, dotted keys replace the object as vLLM does) | `Orchestration/VllmArgsNormalizer.cs`, `Core/JsonFields.cs`, `Core/ModelDefinition.cs` (`SpeculativeDecoding`, `CompilationConfig`, tri-states, `MambaCacheMode`) | `CanonicalConfigTests` |
| Builder emits each owned setting once; hash over the canonical args; legacy labels rechecked from `Cmd` | `VllmArgsBuilder.cs`, `VllmArgsParser.Canonicalize`, `ModelConfigHash.cs`, `RestartPendingRule` + `RemoteModelRuntime` (was `DockerModelRuntime.LaunchedHashAsync`, deleted in MN-047) | `CanonicalConfigTests`, `RestartPendingTests`, `RemoteModelRuntimeTests` |
| Registry migration on load, normalization on save; editor fields + lifted-flag notice | `JsonModelRegistry.cs`, `Pages/ModelEditor.razor` | `CanonicalConfigTests`, `ModelEditorTests` |
| A3 apply writes the normalized definition | `ModelJobService.ApplyTuningCandidateAsync` | `TuningCorrectnessTests` |
| A4/R2-2 bounded recovery | `ModelJobService` (`LaunchCandidateAsync`, `CleanupCandidateAsync`, `RestoreAsync`, `TuningInfrastructureException`) | `TuningCorrectnessTests` |
| A5 pooling models (embeddings benchmark, seqs/batched plan) | `BenchmarkSuite.RunEmbeddingsAsync`, `Core/PoolingMode.cs`, `TuningPlanner.PoolingSteps` | `EmbeddingBenchmarkTests`, `TuningCorrectnessTests` |

## C. Benchmark and scoring

| Plan item | Code | Tests |
|---|---|---|
| C1 workloads (short chat 256/1024 × code/tool/prose, schema-validated tools, cold 16K/64K with a random prefix first, shared prefix with the cache-hit delta) | `BenchmarkSuite.RunWorkloadsAsync`, `WorkloadPrompts.cs`, `JsonSchemaValidator.cs` | `WorkloadSuiteTests` |
| C2 levels N = 1, 16, max num seqs (≤ 64) | `BenchmarkSuite.WorkloadLevels` | `WorkloadSuiteTests` |
| C3/R2-5 presets, renormalized weights, SLO guards (configurable per goal) | `WorkloadProfiles.cs`, `WorkloadScorer.cs`, `ModelJobOptions.Guards` | `WorkloadScoringTests` |
| C4 noise: 3 reps, top-2 + baseline × 5 in random order, ≥ 5 % with non-overlapping ranges | `ModelJobService.ConfirmAsync`, `WorkloadScorer.Range/BeatsBaseline` | `PresetTuningTests` |
| C5 / R2-4 cold vs warm, warm-ups excluded | `BenchmarkSuite` workloads | `WorkloadSuiteTests` |
| Result schema versioning (v1 still loads) | `BenchmarkResult.SchemaVersion`, `TuningRun.SchemaVersion` | `PresetTuningTests.StoredV1Results_StillLoad_AsSchemaVersion1` |

## B. Search

| Plan item | Code | Tests |
|---|---|---|
| Capability gates (MTP layers + image MTP registry, drafter depths by trained block size, Mamba pairing, KV auto by memory, dense > 20 B seqs 1/2, batched incl. saved) | `TuningGates.cs`, `ModelFolderInspector` (`IsMoe`, `IsMambaHybrid`, `SpeculativeBlockSize`, `ParamsBillions`), `MirbadLLMOptions.MtpArchitectures` | `SearchPlannerTests` |
| Phase 1 KV × prefix (both prefix configurations always, R2-3) | `SearchPlanner.Plan` | `SearchPlannerTests` |
| Phase 2 seqs then batched tokens | `SearchPlanner.Plan` | `SearchPlannerTests`, `SearchRunTests` |
| Phase 3 speculative × FULL_AND_PIECEWISE, PIECEWISE follow-ups (failed full / top-2 depths), async; forced PIECEWISE removed | `SearchPlanner.Plan/FollowUps`, `TuningPlanner.Apply` | `SearchPlannerTests`, `SearchRunTests`, `TuningPlannerTests` |
| Refinement (seqs and depth neighbours) + final prefix on/off (R2-3) | `SearchPlanner` (`RefineKey`, `PrefixCheckKey`) | `SearchPlannerTests` |
| De-dup on the effective config; confirmation at the end | `ModelJobService.RunTuningAsync` (`MeasureAllAsync`) | `SearchRunTests` |
| Baseline breaking an absolute guard → relative guard, noted | `WorkloadScorer.RelaxForBaseline` | `SearchPlannerTests`, `SearchRunTests` |

## D. UI

| Plan item | Code | Tests |
|---|---|---|
| D1 goal + tradeoff for every phase/step/value; effective config and runtime downgrades per row | `TuningDescriptions.cs`, `RuntimeLogParser.cs`, `Pages/ModelTune.razor`, `Ui/JobProgressPanel.razor` | `SearchPlannerTests` (catalog coverage, log parsing), `ModelTuneApplyTests` |
| D2 Quick / Full / Custom, dynamic ETA (preview and live) | `SearchPlanner.Quick/Full`, `TuningEstimator`, `ModelJobService.EstimateDurationsAsync`, `JobState.MeasuredPerCandidate` | `SearchPlannerTests`, `SearchRunTests`, `ModelTuneApplyTests` |

## E. Mixed workload, pool pressure, KV pool line

| Item | Code | Tests |
|---|---|---|
| E1 mixed workload: one cold 64K request (16K when the context cannot hold 64K + output), the N=16 short set started 1 s later on top of it; short TTFT p95 and per-request tok/s under load, the long request's own TTFT; `MixedShortTtft` component (baseline ÷ candidate, weight 0.1 in Interactive chat and Agents & tools) and `MixedShortTtftP95Seconds` guard (3 s; off for Throughput and Long documents); schema v3 (v1/v2 still load) | `BenchmarkSuite.MixedAsync`, `MixedWorkloadResult`, `WorkloadProfiles.Mixed`, `WorkloadScorer` | `WorkloadSuiteTests` (slow first token on the long request), `WorkloadScoringTests` |
| E2 pool-pressure guard: `vllm:num_preemptions_total` diffed over the timed workloads and the peak `vllm:kv_cache_usage_perc`, read from the model's `/metrics`; `MaxPreemptions` guard (0; null disables) fails "KV pool too small for this workload: N preemptions", made relative when the baseline preempts, skipped (never failed) when `/metrics` cannot be read | `BenchmarkSuite.ReadPoolAsync`, `BenchmarkResult.Preemptions/KvCacheUsageMax`, `WorkloadScorer.GenerationGuards/RelaxForBaseline`, `Ui/BenchmarkResultView.razor` | `WorkloadSuiteTests`, `WorkloadScoringTests` |
| E3 KV pool line per candidate (report only): "GPU KV cache size" and "Maximum concurrency" from the container log, shown as "KV pool 507k tokens = 7.7 concurrent requests at 65,536 max model len" under each candidate row | `RuntimeLogParser` (`RuntimeReport.KvPoolLine`), `TuningCandidateResult.KvPool`, `Pages/ModelTune.razor` | `SearchPlannerTests`, `ModelTuneApplyTests` |

Classic goals (Balanced, Many agents, Single user) keep the classic plan, suite and scorer through the API; the UI offers the presets.
