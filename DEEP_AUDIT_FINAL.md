# NeuTerradise — Canonical Deep Audit Final

## Stage 1–12 Closure, Remediation Plan, and Regression Prevention Contract

**Repository:** secondshift-dv/neuterradise  
**Canonical branch:** main  
**Audit date:** 2026-09-19  
**Frozen source baseline:** 3f5f7c861fd773336182e4c61871e95c8761a549  
**Pre-documentation live HEAD:** be5d99b4659892de1b8d2697538fd6e29c3c24b5  
**Pre-documentation comparison:** main is two commits ahead of the frozen baseline with zero file differences. The two commits are an unintended Stage 4 source change and its revert.  
**Authority status:** TRUST-CERTIFIED static audit ledger and remediation specification. Historical claims are subordinate to Section 4A falsification classification.  
**Audit status:** CLOSED-DOC  
**Source remediation status:** OPEN — implement only CONFIRMED-SOURCE and CONFIRMED-HARDENING records.  
**Build/package/runtime acceptance:** NOT YET PROVEN  
**Next valid finding ID:** X74

---

# 1. Purpose

This document exists to stop audit drift.

The Deep Audit is finished for the frozen source baseline. The next phase is not another broad audit. The next phase is implementation of the findings in this document, followed by exact regression and runtime verification.

Every concrete finding below has five mandatory layers:

1. **Temuan** — the actual defect or evidence gap.
2. **Root cause / failure path** — why the defect exists and how the failure propagates. For audit-control/evidence-only records, this layer is the exact provenance or evidence cause.
3. **Penyelesaian** — the direct corrective design that must be implemented, or the evidence action required when source remediation is not yet authorized.
4. **Penanggulangan** — the permanent regression guard, invariant, recovery rule, or verification gate that prevents the same class of defect from returning.
5. **Source traceability** — the concrete repository path(s) and symbol/authority that an implementation agent must inspect. Section 4B is normative and satisfies this layer even when the finding body also repeats local paths.

A finding is not source-closed merely because a local symptom was patched. Closure requires the root cause, sibling paths, recovery behavior, traceability authority, and regression guard to converge.

---

# 2. Status semantics

The following status vocabulary is mandatory.

- **CLOSED-DOC** means the audit investigation is complete enough to define the defect, root cause, solution, and prevention contract.
- **OPEN-IMPLEMENTATION** means the source or verification work described by the finding has not yet been accepted as corrected.
- **SOURCE-CLOSED** may be assigned only after the corrective implementation and required regression guards are committed.
- **VERIFIED** may be assigned only after the exact required verification succeeds on the intended source tree or package.
- **RUNTIME-VERIFIED** may be assigned only after executable runtime behavior is proven, not inferred from source inspection.
- **RESOLVED-NOT-REPRODUCED** may be assigned only to an EXECUTION-EVIDENCE-GAP after the exact required executable/artifact evidence disproves the suspected failure. It authorizes no source patch and must retain the evidence record.
- **RESERVED** means a finding ID is permanently unavailable for reuse but does not represent a recoverable concrete finding.

Do not translate CLOSED-DOC into fixed, passed, safe, production-ready, or runtime-verified.

---

# 3. Immutable ledger rules

1. Finding IDs are immutable primary keys.
2. X27, X28, and X29 are permanently RESERVED — UNRECOVERED HISTORICAL IDS.
3. They must never be filled with reconstructed guesses.
4. The old Stage 7 response that reused X06–X09 is superseded for nomenclature.
5. X06–X08 remain Stage 3 findings.
6. X09 remains the Stage 4 same-volume replay finding.
7. Profile Purge FK closure incomplete is canonical X37.
8. New findings after this document start at X74.
9. Do not renumber X01–X63 to make the sequence visually contiguous.
10. A future audit finding must record provenance, evidence, root cause, corrective action, prevention, verification, and status when it is created.

---

# 4. Stage map

| Stage | Scope | Canonical findings |
| --- | --- | --- |
| 1 | Cold-start census and repository coverage | no numbered finding; establishes full baseline |
| 2 | Structural / architecture contracts | X01–X05 |
| 3 | Database / persistence / lifecycle invariants | X06–X08 |
| 4 | Import / Media / Vault / Storage | X09–X18 |
| 5 | Jobs / Scheduler / Concurrency / Recovery | X19–X26 |
| Historical reserved slots | unrecovered IDs | X27–X29 RESERVED |
| 6 | Profiling / Faces | X30–X36 |
| 7 | Profiles / Relations / Trash / Purge | X37 |
| 8 | Application / UI integration | X38–X42 |
| 9 | Updater / Package / Release / Security | X43–X50 |
| 10 | Cross-domain adversarial lifecycle | X51–X55 |
| 11 | Build / Package / Runtime verification | X56–X61 |
| 12 | Convergence / audit-control closure + final coverage reconciliation | X62–X73 |

Concrete ledger records: **70**.  
- **52 CONFIRMED-SOURCE findings**  
- **5 CONFIRMED-HARDENING gaps**  
- **7 EXECUTION-EVIDENCE-GAP records**  
- **4 REJECTED-AS-INDEPENDENT-FINDING records**  
- **2 AUDIT-CONTROL records**  
Reserved historical IDs: **3 (X27–X29)**.

---

# 4A. Trustworthiness certification

This section is the **falsification result**, not a restatement of prior audit conclusions.

Every X record was classified by tracing the current source producer → durable authority → consumer → recovery/terminal path. A historical finding was not retained merely because an earlier chat or audit named it.

## 4A.1 Certified classification

| Class | Count | Meaning |
| --- | ---: | --- |
| CONFIRMED-SOURCE | **52** | A concrete reachable source mismatch/failure path is present. Remediation is authorized. |
| CONFIRMED-HARDENING | **5** | The weaker behavior is real, but the item is security/supply-chain/reproducibility hardening rather than a demonstrated functional failure. |
| EXECUTION-EVIDENCE-GAP | **7** | Static source cannot prove PASS/FAIL; executable artifact/runtime evidence is required. |
| REJECTED-AS-INDEPENDENT-FINDING | **4** | Falsification disproved the original claim or showed it is already owned by another root cause. Do not implement it separately. |
| AUDIT-CONTROL | **2** | Ledger/provenance controls, not application defects. |
| RESERVED | **3 IDs** | X27–X29 are unavailable historical slots and are not findings. |

**52 + 5 + 7 + 4 + 2 = 70 concrete ledger records. X27–X29 are reserved only.**

## 4A.2 Per-finding trust ledger

| ID | Classification | Primary source evidence / disposition |
| --- | --- | --- |
| X01 | `src/Neuterradise.Runtime/Profiles/ProfileAppearanceRules.cs` — canonical Cover media/source/timestamp semantics; `src/Neuterradise.Runtime/Profiles/ProfileAppearanceOperations.cs` — durable Cover source metadata producer; `src/Neuterradise.Runtime/SystemServices/Lifecycle/CriticalIntegrityGate.cs` — startup appearance integrity consumer. |
| X02 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateHandoffService.cs` — exact handoff/manifest digest producer; `src/Neuterradise.Release.Contracts/UpdateManifestAuthority.cs` — shared manifest digest authority; `src/Neuterradise.Updater/Program.cs` and `src/Neuterradise.Updater/ReplacementEngine.cs` — isolated helper digest consumer and pre-replacement byte revalidation; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateRecoveryPlan.cs` / `UpdateStartupRecovery.cs` — durable manifest-authority recovery chain. |
| X03 | `release-contract.json` and `src/Neuterradise.Release.Contracts/ReleaseContract.cs` — canonical deployed model-root contract; `scripts/package-win-x64.ps1` — model deployment producer; `src/Neuterradise.Profiling.Worker/WorkerRuntimeEnvironment.cs` — Worker model-root consumer. |
| X04 | `src/Neuterradise.Runtime/Profiles/ProfileOperations.cs` — rename mutation/operation producer; `src/Neuterradise.Runtime/SystemServices/Jobs/ReconciliationJobAuthority.cs` — transaction-owned durable job authority and startup backfill; `src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs` — startup recovery consumer; `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/ProfileRenameReconciliationJobHandler.cs` — reconciliation executor. |
| X05 | `src/Neuterradise.Runtime/Media/MediaOperations.cs` — OWNER mutation/relocation-operation producer; `src/Neuterradise.Runtime/SystemServices/Jobs/ReconciliationJobAuthority.cs` — transaction-owned durable job authority and startup backfill; `src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs` — startup recovery consumer; `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/OwnerRelocationJobHandler.cs` — relocation executor. |
| X06 | CONFIRMED-SOURCE | Asset Trash deletes OWNER only; DB trigger forbids non-ACTIVE asset while any profile_assets relation remains. |
| X07 | CONFIRMED-SOURCE | Profile Trash sets trashed marker without resolving all profile_assets/active identity required by DB trigger. |
| X08 | CONFIRMED-SOURCE | Profile physical recovery work lacks equivalent restart handling; TrashRecovery handles EXECUTING/IN_TRASH Asset branches, not the paired Profile path. |
| X09 | CONFIRMED-SOURCE | Same-volume replay returns UnexpectedTarget when both source and already-copied target exist before checkpoint recovery. |
| X10 | CONFIRMED-SOURCE | Source bytes are verified on an open stream, but destructive File.Delete(path) remains pathname-based; source identity can be absent and the delete race is not handle-bound. |
| X11 | CONFIRMED-SOURCE | REUSE cleanup reads component metadata from ManagedAssetId/reused target while destructive source authority belongs to CandidateAssetId. |
| X12 | CONFIRMED-SOURCE | ProvisionProfileFolderAsync mutates directory/manifest before DestinationPrepared is checkpointed. |
| X13 | CONFIRMED-SOURCE | LibraryHealthEvaluator verifies primary managed asset bytes/path but does not enumerate/verify required asset_components package members. |
| X14 | CONFIRMED-SOURCE | ModelPackageDiscovery catches GLTF/OBJ/DAE parse/IO failures and can fall through to SelfContained when dependency discovery produced no entries. |
| X15 | REJECTED | Complete package duplicate detection already uses BundleSha256; remaining risks are X14/X72. |
| X16 | REJECTED | Durable acknowledged-missing-dependency IDs are persisted and consumed by VerificationValidator. |
| X17 | CONFIRMED-SOURCE | Import uses AllocateAssetPlan, while RestoreExecutor and MediaOperations directly call PlanAsset, allowing avoidable collision/attention paths. |
| X18 | REJECTED | No reachable production path using naive textual prefix as path authority was proven. |
| X19 | CONFIRMED-SOURCE | Job handlers catch cancellation and return JobExecutionResult.Cancelled; scheduler Pause intent mapping only runs when cancellation exception escapes. |
| X20 | CONFIRMED-SOURCE | ImportFinalizer has its own pass gate; cancellation settlement has no shared per-unit mutation lease covering the whole commit/rollback boundary. |
| X21 | CONFIRMED-SOURCE | ImportWrites.UpdateUnitStateAsync updates by unit ID without expected row-version/legal-transition predicate. |
| X22 | CONFIRMED-SOURCE | ShutdownCoordinator can time out service disposal then finally dispose BootstrapContext/VaultLock while timed-out disposal task may still run. |
| X23 | CONFIRMED-SOURCE | SessionMarkerStore.WriteUncleanAsync has no production caller; only clean marker writing is wired. |
| X24 | CONFIRMED-SOURCE | Start ignores failed ResumeUnitAsync result and still persists focus; Prioritize focuses without lifecycle validation. |
| X25 | CONFIRMED-SOURCE | CapabilityReads counts every applicable capability and every FAILED row as blocking although applicability marks face/search/etc optional. |
| X26 | CONFIRMED-SOURCE | Scheduler shutdown cancels linked handler token; handlers convert that token cancellation to Cancelled and can durably cancel resumable work. |
| X27–X29 | RESERVED | Historical slots only. |
| X30 | CONFIRMED-SOURCE | Profiling dispatcher awaits AnalyzeFaces in the read loop; CancelRequest cannot be read until that handler returns. |
| X31 | CONFIRMED-SOURCE | Worker uses ExpectedSha256 to derive detection keys but does not hash/compare input bytes before decode/inference. |
| X32 | CONFIRMED-SOURCE | IdentityBankProvider caches spaces; FaceDecisionOperations changes identity_samples without invalidating that provider. |
| X33 | CONFIRMED-SOURCE | FaceAnalysisJobHandler persists embedding-bearing detection rows using YuNet ModelId/ModelVersion while embedding space is SFace. |
| X34 | CONFIRMED-SOURCE | ProfilingWorkerProcessHost resets ConsecutiveFailures immediately on Ready handshake, allowing Ready→crash loops to reset the breaker. |
| X35 | CONFIRMED-SOURCE | BuildIdentityIndex sends complete sample bank in one request while protocol enforces a finite single-frame payload limit. |
| X36 | CONFIRMED-SOURCE | ReleaseIndex is after normal matching loop and is not protected by finally/lease cleanup. |
| X37 | CONFIRMED-SOURCE | Purge profile dependency/preflight/delete logic omits import_assignment_clusters candidate_profile_id/decided_profile_id FKs. |
| X38 | CONFIRMED-SOURCE | Profile LoadAsync uses fire-and-return UiDispatch.Run after ConfigureAwait(false); applySucceeded can be tested before queued UI commit executes. |
| X39 | CONFIRMED-SOURCE | Settings InitializeAsync uses ConfigureAwait(false) on configuration load then raises UI-bound properties directly. |
| X40 | CONFIRMED-SOURCE | ApplyLanguageAsync mutates CurrentLanguage/SurfaceText before durable configuration save succeeds. |
| X41 | CONFIRMED-SOURCE | LatestValueAction drains with ConfigureAwait(false); theme/presentation apply path can therefore enter live presentation from a worker continuation. |
| X42 | CONFIRMED-SOURCE | Several queued UiDispatch.Run callbacks do not recheck route lifetime/generation inside the callback, e.g. final ShowReady path. |
| X43 | `.github/workflows/release.yml` — custom `NUGET_PACKAGES` producer; `scripts/package-win-x64.ps1` — package verification now resolves the effective `NUGET_PACKAGES` authority before falling back to the user-profile default. |
| X44 | `release-contract.json` and `src/Neuterradise.Release.Contracts/ReleaseContract.cs` — single required-release-member authority; `scripts/package-win-x64.ps1` — package producer/enforcer; `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageValidator.cs` and `src/Neuterradise.Updater/ReplacementEngine.cs` — runtime/helper consumers. |
| X45 | CONFIRMED-HARDENING | UpdateTrustPolicy explicitly states publisher identity verification is not part of v0.0.1 trust contract. |
| X46 | CONFIRMED-HARDENING | release workflow grants contents:write while build job consumes movable major action tags. |
| X47 | CONFIRMED-SOURCE | Successful updater path has no convergent terminal cleanup owner for operation payload/tools and replacement backup/staging residues. |
| X48 | CONFIRMED-SOURCE | UpdateStateStore.LoadAsync can throw corrupt JSON before UpdateStartupRecovery's typed plan/handoff corruption catches. |
| X49 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateManifest.cs` — strict candidate/minimum version syntax and ordering; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateTrustPolicy.cs` — installed-version compatibility enforcement including explicit cross-major minimum authority. |
| X50 | `global.json` and `.github/workflows/release.yml` — exact .NET SDK 10.0.401 authority; `scripts/build.ps1` — exact-SDK fail-closed check and dependency-declaration provenance digest; `Directory.Build.props` / project files — declared dependency authority. **Still open:** no checked-in NuGet `packages.lock.json` graph / locked restore authority exists yet. |
| X51 | REJECTED | Current JobRecovery exhausts final attempts instead of intentionally requeueing them. |
| X52 | CONFIRMED-SOURCE | AppBootstrapper disposes BootstrapContext/VaultLock on callback failure before App-level CleanupPartialStartupAsync disposes runtime created during prewarm. |
| X53 | CONFIRMED-SOURCE | Startup blocks only Recovery.HasFatal; CriticalIntegrityGate does not cover path_state NEEDS_ATTENTION, so path authority ambiguity can remain writable. |
| X54 | CONFIRMED-SOURCE | App constructs ShutdownCoordinator without stopAcceptingCommands; no process-wide mutation admission gate closes at shutdown start. |
| X55 | CONFIRMED-SOURCE | Updater waits parent at most 60s while app performs finalizer/UI/import work before ShutdownCoordinator's bounded shutdown phase. |
| X56 | EXECUTION-EVIDENCE-GAP | No accepted exact-SHA canonical Release build result for the remediated tree. |
| X57 | EXECUTION-EVIDENCE-GAP | Package structural validation is not executable startup evidence. |
| X58 | EXECUTION-EVIDENCE-GAP | Isolated deterministic runtime verification harness is required. |
| X59 | EXECUTION-EVIDENCE-GAP | Packaged Worker/OpenCV/YuNet/SFace inference has not been executed as acceptance evidence. |
| X60 | EXECUTION-EVIDENCE-GAP | Disposable updater replacement/recovery scenarios require process/filesystem execution. |
| X61 | EXECUTION-EVIDENCE-GAP | Cross-domain lifecycle matrix must become an executable regression gate. |
| X62 | AUDIT-CONTROL | X27–X29 provenance cannot be recovered; IDs remain reserved. |
| X63 | AUDIT-CONTROL | Historical Stage 7 ID collision is superseded; canonical IDs are frozen. |
| X64 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs` — explicit canonical ZIP-entry segment validation, full-path containment proof, shared reparse-aware authority, and immediate pre-write re-resolution. |
| X65 | EXECUTION-EVIDENCE-GAP | Whether canonical Compress-Archive emits directory entries rejected by the stager must be proven with the exact ZIP artifact. |
| X66 | CONFIRMED-SOURCE | Cancelled completion branch does not invoke immediate terminal observer; normal correctness can wait for terminal reconciliation polling. |
| X67 | CONFIRMED-SOURCE | MediaActivationArbiter delays first-click action by GetDoubleClickTime/500ms fallback. |
| X68 | CONFIRMED-SOURCE | Shell work/status projection is driven by a 750ms PeriodicTimer instead of normal change signaling. |
| X69 | CONFIRMED-SOURCE | focused-import priority resolves jobs through candidate_asset_id only; shared reused_asset_id work has no durable per-unit interest model. |
| X70 | CONFIRMED-SOURCE | UI copy command synchronously reaches Win32Clipboard.SetText, whose retry loop uses Thread.Sleep. |
| X71 | `release-contract.json` — project-controlled FFmpeg mirror location; `scripts/package-win-x64.ps1` — mirror-first exact-SHA retrieval with exact upstream fallback. **Still open:** the declared project mirror Release asset has not yet been published. |
| X72 | CONFIRMED-SOURCE | REUSE decision is not transactionally revalidated against current active/content/package authority immediately before commit. |
| X73 | CONFIRMED-SOURCE | cancellation exclusivity query excludes reused_asset_id only within the same ImportUnit and can trash an asset another live import reuses. |

## 4A.3 Trust rules

1. Only **CONFIRMED-SOURCE** and **CONFIRMED-HARDENING** items authorize source changes.
2. **EXECUTION-EVIDENCE-GAP** items authorize verification work, not speculative source patches.
3. **REJECTED** items must not appear in implementation queues.
4. Any future challenge to an X item must be resolved by source/executable evidence and recorded as a status change; historical wording alone is not authority.
5. A new X74+ is allowed only for a distinct root cause not already owned by this ledger.
6. Static certification does not claim zero possible bugs. It claims that the ledger no longer treats unproven historical assertions as source defects and that every retained source finding has a concrete code-path basis.


# 4B. Normative source traceability index

This index is the canonical implementation locator for the ledger. It exists so a remediation agent does not have to rediscover where a finding lives or infer the intended authority from prose.

Rules:

1. Every concrete X record has an explicit trace target below.
2. For **CONFIRMED-SOURCE/HARDENING**, the listed path/symbol set is the minimum read set before editing. Callers and sibling implementations must still be traced before mutation.
3. For **EXECUTION-EVIDENCE-GAP**, the listed paths identify the artifact/runtime boundary to execute; they do not authorize speculative source changes.
4. For **REJECTED**, the listed paths are falsification evidence only.
5. A path listed here does not imply that every file requires modification.
6. If implementation proves the real authority moved, update this index in the same commit that changes the finding's closure record.

| ID | Canonical trace target |
| --- | --- |
| X01 | `src/Neuterradise.Runtime/Profiles/ProfileAppearanceRules.cs` — Cover eligibility; `src/Neuterradise.Runtime/SystemServices/Lifecycle/CriticalIntegrityGate.cs` — startup appearance integrity SQL. |
| X02 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateHandoffService.cs` — validated handoff producer; `src/Neuterradise.Updater/ReplacementEngine.cs` — helper replacement consumer; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateRecoveryPlan.cs` — durable recovery authority. |
| X03 | `scripts/package-win-x64.ps1` — deployed model topology; `src/Neuterradise.Profiling.Worker/WorkerRuntimeEnvironment.cs` and `src/Neuterradise.Profiling.Protocol/ProfilingRuntimeEnvironment.cs` — runtime model-root contract. |
| X04 | `src/Neuterradise.Runtime/Profiles/ProfileOperations.cs` — rename mutation/optional enqueue; `src/Neuterradise.Runtime/Profiles/ProfileDetailViewModel.cs` — production caller; `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/ProfileRenameReconciliationJobHandler.cs` — reconciliation consumer. |
| X05 | `src/Neuterradise.Runtime/Media/MediaOperations.cs` — OWNER mutation/optional relocation enqueue; `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/OwnerRelocationJobHandler.cs` — relocation consumer. |
| X06 | `src/Neuterradise.Runtime/Trash/TrashCoordinator.cs` — `CommitAssetTrashTransitionAsync`; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0001_initial.sql` — `trg_assets_nonactive_has_no_relations`; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs` — paired restore semantics. |
| X07 | `src/Neuterradise.Runtime/Trash/TrashCoordinator.cs` — `CommitProfileTrashMarkerAsync`; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0001_initial.sql` — `trg_profiles_trash_has_no_active_relations`; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs` — paired restore. |
| X08 | `src/Neuterradise.Runtime/Trash/TrashCoordinator.cs`, `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs`, and `src/Neuterradise.Runtime/SystemServices/Recovery/TrashRecovery.cs` — Profile Trash/Restore physical operation and restart recovery. |
| X09 | `src/Neuterradise.Runtime/SystemServices/Storage/ManagedMoveExecutor.cs` — existing-target/`UnexpectedTarget` replay classification; import commit callers under `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs`. |
| X10 | `src/Neuterradise.Runtime/SystemServices/Storage/SourceCleanupExecutor.cs` — destructive MOVE cleanup; `src/Neuterradise.Runtime/SystemServices/Storage/SourceIdentityHelper.cs` — stable source identity. |
| X11 | `src/Neuterradise.Runtime/SystemServices/Storage/SourceCleanupExecutor.cs` — candidate vs reused-target component authority; `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs` — REUSE commit inputs. |
| X12 | `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs` — `PrepareDestinationAsync` / `ProvisionProfileFolderAsync` and DestinationPrepared checkpoint ordering. |
| X13 | `src/Neuterradise.Runtime/Maintenance/LibraryHealthEvaluator.cs` — asset health aggregation; component authority originates from database asset-component records. |
| X14 | `src/Neuterradise.Runtime/Media/Model/ModelPackageContracts.cs` — `ModelPackageDiscovery.Discover`; `src/Neuterradise.Runtime/Import/Preparation/ImportPreparationCoordinator.cs` — discovery consumer/admission. |
| X15 | REJECTED evidence: `src/Neuterradise.Runtime/Import/Preparation/ImportPreparationCoordinator.cs`, `src/Neuterradise.Runtime/Import/Preparation/ExactDuplicateDetector.cs`, `src/Neuterradise.Runtime/Import/Verification/VerificationValidator.cs`. |
| X16 | REJECTED evidence: `src/Neuterradise.Runtime/Import/Verification/VerificationValidator.cs` — persisted acknowledged-missing-dependency consumption. |
| X17 | `src/Neuterradise.Runtime/SystemServices/Storage/ManagedPathPlanner.cs` — `PlanAsset` / allocator authority; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs` and `src/Neuterradise.Runtime/Media/MediaOperations.cs` — direct-plan callers. |
| X18 | REJECTED evidence: `src/Neuterradise.Runtime/SystemServices/Storage/RootPathRules.cs` plus inspected update/storage/recovery consumers; no reachable naive-prefix authority was proven. |
| X19 | `src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs` — interruption completion semantics; `src/Neuterradise.Runtime/SystemServices/Jobs/JobCancellationOperations.cs` — control intent; `JobLeaseStore.cs` and `src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs` — resumable shutdown interruption settlement; handlers returning `JobExecutionResult.Cancelled` are semantic-input producers only. |
| X20 | `src/Neuterradise.Runtime/SystemServices/Database/ImportUnitMutationCoordinator.cs` — canonical per-ImportUnit mutation lease; `src/Neuterradise.Runtime/Import/ImportUnitControlAuthority.cs` — Pause/Start/Retry/Cancel/Clear-history user-command authority; `ImportFinalizer.cs`, `ImportCancellationSettlement.cs`, `ImportCommitCoordinator.cs`, `ImportPublicationCoordinator.cs`, and `src/Neuterradise.Runtime/SystemServices/Recovery/ImportRecovery.cs` — mutation actors sharing the same unit serialization boundary. |
| X21 | `src/Neuterradise.Runtime/Import/ImportReadModels.cs` — canonical lifecycle lattice; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportWrites.cs` — monotonic state-transition/CAS authority; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportUnitWrites.cs` — pause/resume/cancel/retry predicates plus atomic `FAILED_RETRYABLE -> INTAKE` / HashAsset re-arm projection; callers in Finalizer/control/publication/recovery. |
| X22 | `src/Neuterradise.Runtime/SystemServices/Jobs/JobDispatcher.cs` — worker graceful-wait/cancel/terminal-join authority; `src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs` and `src/Neuterradise.Runtime/SystemServices/Lifecycle/ProductionRuntimeRegistry.cs` — nested scheduler/runtime disposal ownership; `src/Neuterradise.Runtime/SystemServices/Lifecycle/ShutdownCoordinator.cs` — service termination before `BootstrapContext` disposal; `src/Neuterradise.Runtime/SystemServices/Recovery/VaultLock.cs` — final lock lifetime. |
| X23 | `SessionMarkerStore.cs` — UNCLEAN/CLEAN persistence; `AppBootstrapper.cs` — UNCLEAN ordering; `BootstrapContext.cs` — SessionId/Generation ownership; `ShutdownCoordinator.cs` — matching CLEAN ordering. |
| X24 | `src/Neuterradise.Runtime/Import/ImportUnitControlAuthority.cs` — Start/Pause/Prioritize command authority; `src/Neuterradise.Runtime/SystemServices/Jobs/ImportPriorityOperations.cs` and `src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs` — lifecycle-eligible focus persistence; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportUnitWrites.cs` — stale-safe Pause/Resume projection where Asset-job mutation is subordinate to a successful unit transition. |
| X25 | `src/Neuterradise.Runtime/SystemServices/Database/Reads/CapabilityReads.cs` — required/terminal aggregation; `src/Neuterradise.Runtime/Import/Preparation/Stage2CompletionHandler.cs` — readiness projection; `src/Neuterradise.Runtime/SystemServices/Database/Writes/CapabilityWrites.cs` — capability state. |
| X26 | `JobScheduler.cs` — scheduler shutdown intent/catch ordering; `JobLeaseStore.cs` and `JobWrites.cs` — RUNNING→PENDING shutdown settlement; `StorageJobResultMapper.cs` and `HashAssetJobHandler.cs` remain cancellation-result producers normalized by the scheduler. |
| X30 | `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs` — serialized read loop, `HandleAnalyzeFacesAsync`, and `CancelRequest`. |
| X31 | `src/Neuterradise.Profiling.Worker/FaceAnalysis/FaceAnalyzer.cs` — `ExpectedSha256` analysis path; `src/Neuterradise.Profiling.Worker/FaceAnalysis/OpenCvFaceImageSource.cs` — bytes decoded for inference. |
| X32 | `src/Neuterradise.Runtime/Faces/IdentityBankProvider.cs` — catalog-scoped provider registry, per-space generation and invalidation; `src/Neuterradise.Runtime/Faces/FaceDecisionOperations.cs` and `src/Neuterradise.Runtime/SystemServices/Database/Writes/FaceWrites.cs` — committed sample mutations and post-commit invalidation. |
| X33 | `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/FaceAnalysisJobHandler.cs` — YuNet detector persistence; `src/Neuterradise.Runtime/Faces/EmbeddingSpaceKey.cs`, `src/Neuterradise.Runtime/Faces/FaceDecisionOperations.cs`, `src/Neuterradise.Runtime/SystemServices/Database/Writes/FaceWrites.cs`, and migration `0016_r5_face_embedding_provenance.sql` — SFace embedding provenance authority. |
| X34 | `src/Neuterradise.Runtime/SystemServices/Jobs/ProfilingWorkerProcessHost.cs` — `ConsecutiveFailures`, Ready handshake, restart breaker. |
| X35 | `src/Neuterradise.Profiling.Protocol/ProfilingProtocol.cs` / `ProfilingContracts.cs` — v3 frame/chunk contract; `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/FaceAnalysisJobHandler.cs` — encoded-size-aware producer; `src/Neuterradise.Profiling.Worker/IdentityMatching/IdentityIndexCache.cs` and dispatcher — ordered chunk consumer/final publisher. |
| X36 | `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/FaceAnalysisJobHandler.cs` — Build/Match/finally cleanup lifetime; `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs` and `IdentityIndexCache.cs` — acknowledged ReleaseIndex plus loaded/pending invalidation. |
| X37 | `src/Neuterradise.Runtime/Trash/PurgeExecutor.cs` and `src/Neuterradise.Runtime/Trash/PurgePlan.cs` — purge dependency/preflight/delete authority; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0003_import_assignment_review.sql` — assignment-cluster FK schema. |
| X38 | `src/Neuterradise.Runtime/Profiles/ProfileDetailViewModel.cs` — `LoadAsync`, queued `UiDispatch.Run`, final `ShowReady`. |
| X39 | `src/Neuterradise.Runtime/Settings/SettingsViewModel.cs` — `InitializeAsync` and UI-bound property/collection mutation after background reads. |
| X40 | `src/Neuterradise.Runtime/Settings/SettingsViewModel.cs` — `ApplyLanguageAsync`; `src/Neuterradise.Runtime/SystemServices/Storage/AppConfigurationStore.cs` — durable preference authority. |
| X41 | `src/Neuterradise.Runtime/Settings/SettingsViewModel.cs` — `LatestValueAction<T>` preference pipelines; `src/Neuterradise.App/Ui/Foundation/ThemeRuntime.cs` — live resource mutation. |
| X42 | `src/Neuterradise.Runtime/Profiles/ProfileDetailViewModel.cs` — queued route callbacks; `src/Neuterradise.Runtime/SystemServices/UiPrimitives.cs` — `UiDispatch` primitive. |
| X43 | `.github/workflows/release.yml` — custom `NUGET_PACKAGES`; `scripts/package-win-x64.ps1` — package-root lookup/verification. |
| X44 | `scripts/package-win-x64.ps1` — package construction/manifest members; `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageValidator.cs` — accepted required membership. |
| X45 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateTrustPolicy.cs` — update trust decision/publisher-authenticity boundary. |
| X46 | `.github/workflows/release.yml` — job permissions and action refs. |
| X47 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateRecoveryPlan.cs`, `src/Neuterradise.Runtime/SystemServices/Updates/UpdateStartupRecovery.cs`, and `src/Neuterradise.Updater/ReplacementEngine.cs` — operation payload/backup/staging terminal cleanup ownership. |
| X48 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateStateStore.cs` — persisted-state parsing; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateStartupRecovery.cs` — typed recovery consumer. |
| X49 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdateManifest.cs` — `MinimumCompatibleVersion`; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateTrustPolicy.cs` — compatibility decision. |
| X50 | `global.json`, `.github/workflows/release.yml`, and project dependency declarations — canonical SDK/dependency resolution authority. |
| X51 | REJECTED evidence: `src/Neuterradise.Runtime/SystemServices/Recovery/JobRecovery.cs` — final-attempt reconcile/exhaustion path. |
| X52 | `AppBootstrapper.cs` — rollback-before-context-disposal contract; `StartupResult.cs` — retained-authority fail-closed result; `BootstrapContext.cs` — VaultLock ownership; `src/Neuterradise.App/App.xaml.cs` — strict runtime rollback and non-retryable retained-lock surface. |
| X53 | `src/Neuterradise.Runtime/SystemServices/Lifecycle/AppBootstrapper.cs`, `src/Neuterradise.Runtime/SystemServices/Lifecycle/CriticalIntegrityGate.cs`, and `src/Neuterradise.Runtime/SystemServices/Recovery/StorageRecovery.cs` — recovery severity vs writable-startup safety. |
| X54 | `src/Neuterradise.Runtime/SystemServices/Database/CatalogMutationAdmissionGate.cs` — process-wide command admission/drain authority; `src/Neuterradise.App/App.xaml.cs` and `ShutdownCoordinator.cs` — synchronous Close/drain ordering; `src/Neuterradise.Runtime/Import/ImportUnitControlAuthority.cs` — admitted Retry/Clear-history routing added by the post-review correction; all mutating Profile/Media/Trash/Import/Verify/Face/Related/Settings/Taxonomy/Update command boundaries consume or delegate through admission leases. |
| X55 | `UpdateCoordinator.cs` and `UpdateManifest.cs` — shared absolute update-shutdown deadline; `src/Neuterradise.App/App.xaml.cs` and `ShutdownCoordinator.cs` — remaining-budget consumption; `UpdateHandoffService.cs`, `src/Neuterradise.Updater/ReplacementEngine.cs`, `UpdateRecoveryPlan.cs`, and `UpdateStartupRecovery.cs` — handoff, parent wait, deterministic pre-mutation deferral/recovery. |
| X56 | EVIDENCE boundary: `AGENTS.md`, `scripts/build.ps1`, `.github/workflows/release.yml`, and exact remediated source SHA. |
| X57 | EVIDENCE boundary: `scripts/package-win-x64.ps1`, produced ZIP, extracted layout, and `src/Neuterradise.App/App.xaml.cs` startup path. |
| X58 | EVIDENCE boundary: `src/Neuterradise.Runtime/SystemServices/Lifecycle/AppBootstrapper.cs`, `src/Neuterradise.Runtime/SystemServices/Storage/AppConfigurationStore.cs`, `src/Neuterradise.Runtime/SystemServices/Storage/VaultPaths.cs`, and a disposable harness that does not yet exist on the frozen tree. |
| X59 | EVIDENCE boundary: `scripts/package-win-x64.ps1`; `src/Neuterradise.Profiling.Worker/ProfilingWorkerHost.cs`, `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs`, and `src/Neuterradise.Profiling.Worker/FaceAnalysis/FaceAnalyzer.cs`; `src/Neuterradise.Profiling.Protocol/ProfilingContracts.cs` and `src/Neuterradise.Profiling.Protocol/ProfilingProtocol.cs`; plus the packaged YuNet/SFace artifacts. |
| X60 | EVIDENCE boundary: `src/Neuterradise.Updater/ReplacementEngine.cs`, `src/Neuterradise.Runtime/SystemServices/Updates/UpdateRecoveryPlan.cs`, `src/Neuterradise.Runtime/SystemServices/Updates/UpdateStartupRecovery.cs`, and a disposable InstallRoot. |
| X61 | EVIDENCE boundary: finding-specific paths for X01–X60 plus the isolated regression harness required by Section 16; no such complete cross-domain harness exists on the frozen tree. |
| X62 | AUDIT-CONTROL authority: this `DEEP_AUDIT_FINAL.md` ledger and recoverable historical provenance only; X27–X29 have no fabricated source mapping. |
| X63 | AUDIT-CONTROL authority: this `DEEP_AUDIT_FINAL.md` immutable ID register and historical Stage 7 alias record. |
| X64 | `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs` — extraction sink; `src/Neuterradise.Runtime/SystemServices/Storage/RootPathRules.cs` — containment/reparse authority; `.github/workflows/security.yml` — static-analysis gate. |
| X65 | EVIDENCE boundary: `scripts/package-win-x64.ps1` exact canonical ZIP and `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs` exact staging consumer. |
| X66 | `JobScheduler.cs` — immediate terminal observer for running cancelled completion; `JobCancellationOperations.cs` — immediate observer for idle cancellation; `Stage2CompletionHandler.cs` — capability/dependency projection; `ProductionRuntimeRegistry.cs` — observer wiring plus reconciliation fallback. |
| X67 | `src/Neuterradise.Runtime/Media/MediaGridViewModel.cs` — `MediaActivationArbiter` and double-click timeout arbitration. |
| X68 | `src/Neuterradise.Runtime/SystemServices/Lifecycle/ProductionRuntimeRegistry.cs` — `PublishStatusLoopAsync` / 750 ms `PeriodicTimer`. |
| X69 | `src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs` — focused-import job scoping; `src/Neuterradise.Runtime/SystemServices/Jobs/ImportPriorityOperations.cs`; `src/Neuterradise.Runtime/Import/Preparation/Stage2CompletionHandler.cs` — shared reused-asset consumer semantics. |
| X70 | `src/Neuterradise.Runtime/SystemServices/Win32Clipboard.cs` — synchronous `SetText` retry/`Thread.Sleep` path and UI callers. |
| X71 | `scripts/package-win-x64.ps1` — pinned FFmpeg artifact identity, upstream URL, cache/source fallback. |
| X72 | `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs` — REUSE final commit/retire/link ordering; `src/Neuterradise.Runtime/Import/Preparation/ExactDuplicateDetector.cs` — canonical duplicate identity. |
| X73 | `src/Neuterradise.Runtime/Import/ImportCancellationSettlement.cs` — `ReadExclusiveActiveAssetIdsAsync`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs` and X69 authority — cross-import/shared consumer state. |


# 5. Global invariants

The remediation must preserve these invariants across every finding.

## 5.1 Authority

- Database authority, filesystem authority, package authority, and runtime authority must never silently disagree.
- Derived manifests, UI projections, caches, and recovery journals must never become a competing source of truth.
- A mutation may become terminal only after the durable authority that defines that terminal state is committed.
- A recovery decision must be based on durable evidence, not on optimistic assumptions about a prior process.

## 5.2 Filesystem and Vault safety

- Vault content is outside application deployment lifecycle.
- No update, installer, uninstaller, package process, cleanup path, or recovery path may mutate the Vault unless the operation is explicitly a Vault-domain operation.
- Any physical move/delete must be bound to the object and authority that were verified.
- Path safety must use canonical full-path containment and reparse-aware rules; string prefix checks are not authority.
- Replay after crash must be idempotent.

## 5.3 Scheduler and lifecycle

- Pause, Cancel, Shutdown, failure, retry exhaustion, and normal success are distinct semantic intents/outcomes.
- Command admission must close before shutdown quiescence begins.
- Writers must stop before VaultLock is released.
- Durable jobs that should resume after restart must never be converted into permanent cancellation merely because the process exited.
- Terminal state writes must be monotonic and stale-writer safe.

## 5.4 Profiling

- One deployment/model-root authority is shared by app, worker, package, and release manifest.
- Input bytes are verified against the expected hash before inference.
- YuNet and SFace provenance are recorded accurately and separately.
- Cancellation must be observable while expensive inference is running.
- Worker crash limits, identity caches, and worker indexes must have deterministic lifecycle and cleanup.

## 5.5 UI

- Persistent state must commit before the UI advertises a successful durable change.
- ObservableCollection and UI-bound property mutations occur on the UI dispatcher.
- Asynchronous route results require generation/lifetime fencing.
- Navigation away, dispose, scroll, or route replacement must invalidate stale callbacks.

## 5.6 Release trust

The trust chain is:

manifest authority → package membership → package hashes → publisher authenticity → compatibility policy → staged payload revalidation → replacement → post-replacement verification → terminal cleanup.

No link may be skipped.

---

# 6. Stage 2 — Structural / Architecture Findings

## X01 — Cover video-frame accepted by appearance rules but rejected by startup integrity

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Profile appearance rules accept IMAGE and VIDEO as valid Cover sources and explicitly model VIDEO as a VideoFrame source. CriticalIntegrityGate currently treats an active Cover reference as valid only when the referenced asset media_type is IMAGE.

Relevant contracts:

- src/Neuterradise.Runtime/Profiles/ProfileAppearanceRules.cs
- src/Neuterradise.Runtime/SystemServices/Lifecycle/CriticalIntegrityGate.cs

A valid profile can therefore be saved with a video-frame Cover and later be rejected during startup integrity evaluation.

### Root cause / failure path

Two independent eligibility authorities exist:

1. ProfileAppearanceRules permits a video asset to provide a deterministic Cover frame.
2. CriticalIntegrityGate embeds a stricter SQL rule that accepts only IMAGE for cover_asset_id.

Flow:

valid VIDEO Cover selection → durable profile appearance state → normal shutdown → next startup → CriticalIntegrityGate → ACTIVE_APPEARANCE_REFERENCE violation → valid state becomes startup-blocking.

### Penyelesaian

Create one canonical Cover eligibility contract and consume it from both mutation validation and startup integrity.

At minimum:

- distinguish asset media eligibility from visual source kind;
- validate persisted cover source metadata consistently;
- allow VIDEO only when the persisted Cover source semantics are a valid VideoFrame;
- keep UNKNOWN profile restrictions unchanged;
- reject missing, trashed, non-ACTIVE, or incompatible referenced assets.

Do not solve this by merely loosening the SQL to any ACTIVE asset.

### Penanggulangan

Regression matrix:

- IMAGE Cover survives shutdown/startup.
- VIDEO VideoFrame Cover survives shutdown/startup.
- VIDEO with invalid/missing Cover source metadata is rejected.
- trashed/non-ACTIVE Cover asset is rejected.
- UNKNOWN profile with Cover remains invalid.
- ProfileAppearanceRules and CriticalIntegrityGate must be proven to share the same eligibility semantics.

### Verification

Create persisted valid and invalid appearance fixtures, restart through the actual integrity gate, and assert exact acceptance/rejection.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Lifecycle/CriticalIntegrityGate.cs`  
**Traceability checked:** `ProfileAppearanceRules.IsCoverMediaTypeEligible`, `ResolveCoverSourceKind`, `IsCoverVisualSourceValid`; `ProfileAppearanceOperations.SetCoverAssetAsync` / durable `ProfileAppearanceOverrides`; startup `CriticalIntegrityGate.CheckActiveAppearanceReferencesAsync`; appearance DB trigger semantics.  
**Root-cause correction:** Startup integrity no longer hard-codes IMAGE-only Cover authority. It consumes the same image/video-frame semantics as the write path and requires durable VideoFrame source/timestamp evidence for VIDEO covers.  
**Regression guard:** Every startup integrity evaluation parses the durable appearance overrides and calls `ProfileAppearanceRules.IsCoverVisualSourceValid`; ambiguous legacy VIDEO Covers fail closed instead of being inferred.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; no build, test, or application execution was performed in R1.  
**Dependency findings checked:** X06, X07, X08, X37, X38, X42  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Runtime/startup execution remains R8 evidence.

## X02 — Updater helper loses manifest/hash authority after handoff

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

The application-side UpdateHandoffService validates the manifest and package before writing handoff state. The updater helper later deserializes its own HandoffData containing operation ID, payload path, install root, and Vault root, but no manifest authority.

Relevant paths:

- src/Neuterradise.Runtime/SystemServices/Updates/UpdateHandoffService.cs
- src/Neuterradise.Updater/ReplacementEngine.cs
- src/Neuterradise.Runtime/SystemServices/Updates/UpdateRecoveryPlan.cs

The helper that performs replacement therefore cannot independently prove that the bytes it copies from user-writable staging are still the bytes validated by the application.

### Root cause / failure path

Manifest authority terminates at the process boundary.

validated staging → handoff persisted → parent exits → staging bytes change or are replaced → updater copies payload → InstallRoot replacement proceeds without manifest/hash revalidation.

Path containment protects location, not content identity.

### Penyelesaian

Make the manifest/hash set part of the durable helper authority.

The updater helper must, immediately before replacement:

- load the exact operation manifest or immutable digest authority;
- validate required membership;
- hash the exact staged files to be copied;
- reject additions/removals when policy requires closed membership;
- reject any mismatch before InstallRoot is moved;
- persist the validated manifest identity in recovery state.

Recovery must use the same manifest authority rather than inventing a weaker validation path.

### Penanggulangan

- Mutation test: alter one staged byte after App validation but before helper replacement; replacement must fail closed.
- Add/remove required file tests.
- Replace handoff or manifest file tests.
- Crash/restart recovery must retain the same manifest authority.
- No helper path may perform replacement with only directory/path validation.

### Verification

Disposable InstallRoot E2E test with deliberate staging mutation between handoff and helper replacement.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Updates/UpdateManifest.cs`; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateHandoffService.cs`; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateRecoveryPlan.cs`; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateStartupRecovery.cs`; `src/Neuterradise.Updater/Program.cs`; `src/Neuterradise.Updater/ReplacementEngine.cs`; `src/Neuterradise.Release.Contracts/UpdateManifestAuthority.cs`; `src/Neuterradise.Release.Contracts/ReleaseContract.cs`; `src/Neuterradise.Runtime/Neuterradise.Runtime.csproj`; `src/Neuterradise.Updater/Neuterradise.Updater.csproj`; `release-contract.json`.  
**Traceability checked:** Handoff producer → exact handoff bytes → command-line digest authority → helper parser → manifest digest → staged-copy membership/hash validation → immediate pre-InstallRoot validation → recovery journal → startup replacement validation.  
**Root-cause correction:** The updater helper now receives immutable handoff and manifest-authority digests, verifies the exact serialized handoff, carries the approved manifest itself, hashes every staged approved file, checks exact membership/control manifest/reparse ancestry, revalidates immediately before moving InstallRoot, and persists the manifest digest through recovery.  
**Regression guard:** Helper execution fails closed on changed handoff bytes, changed manifest authority, missing/extra staged members, byte-length/SHA mismatch, control-manifest contradiction, or reparse-point ancestry; startup recovery also requires the journal digest to match the durable handoff manifest.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; updater replacement/recovery execution remains R8.  
**Dependency findings checked:** X44, X45, X47, X48, X49, X55, X60, X64, X65  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Executable updater crash/replacement matrix remains R8 evidence.

## X03 — Packaged profiling model root disagrees with Worker resolver

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Packaging installs YuNet and SFace under workers/models, while WorkerRuntimeEnvironment resolves ModelsRoot as InstallRoot/models.

Relevant paths:

- scripts/package-win-x64.ps1
- src/Neuterradise.Profiling.Worker/WorkerRuntimeEnvironment.cs
- src/Neuterradise.Profiling.Protocol/ProfilingRuntimeEnvironment.cs

### Root cause / failure path

The package and worker each define their own model-root topology.

package succeeds → workers/models/yunet and workers/models/sface exist → worker receives InstallRoot → resolves InstallRoot/models → models are reported unavailable although the package contains them.

### Penyelesaian

Define one model-root authority.

Preferred contract:

- the host supplies an explicit worker/model root appropriate to the deployed topology; or
- the shared runtime contract defines the exact relative path used by both package and worker.

Update:

- package manifest paths,
- worker resolution,
- worker Hello/model availability,
- release validator,
- provenance metadata.

No working-directory or source-tree fallback.

### Penanggulangan

- Package fixture must start the packaged Worker from the extracted package.
- Hello must report both pinned models available.
- Hash of each loaded model must equal release authority.
- Moving the working directory must not change model resolution.
- A missing or hash-mismatched model must degrade only the intended capability and fail closed.

### Verification

Packaged Worker handshake plus one real YuNet/SFace inference in the packaged layout.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `release-contract.json`; `src/Neuterradise.Release.Contracts/Neuterradise.Release.Contracts.csproj`; `src/Neuterradise.Release.Contracts/ReleaseContract.cs`; `src/Neuterradise.Profiling.Worker/Neuterradise.Profiling.Worker.csproj`; `src/Neuterradise.Profiling.Worker/WorkerRuntimeEnvironment.cs`; `scripts/package-win-x64.ps1`; `NeuTerradise.sln`.  
**Traceability checked:** Package model deployment under InstallRoot → shared embedded release contract → Worker InstallRoot environment → `WorkerRuntimeEnvironment.ModelsRoot` → YuNet/SFace consumers.  
**Root-cause correction:** `workers/models` is now one canonical model-root value in `release-contract.json`, consumed by both packaging and the Worker instead of being independently reconstructed.  
**Regression guard:** Packaging must produce the contract-declared YuNet/SFace members and Worker startup resolves the embedded identical `modelsRelativeRoot`; missing/invalid contract data fails closed.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; packaged Worker inference remains R8/X59.  
**Dependency findings checked:** X30, X31, X33, X35, X44, X59  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Packaged Worker/model E2E remains R8 evidence.

## X04 — Profile rename reconciliation obligations lack guaranteed production enqueue

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

The repository contains ProfileRenameReconciliationJobHandler and PathReconciler support, but the durable rename obligation is not guaranteed to be enqueued through a single mandatory production path at the moment the mutation creates the obligation.

### Root cause / failure path

Mutation and reconciliation scheduling are separate authorities.

profile name/path authority changes → durable placement requires reconciliation → no guaranteed job enqueue → stale filesystem path persists until unrelated recovery/restart eventually notices it.

### Penyelesaian

Make the durable rename mutation and scheduling obligation one production contract.

- Persist the rename operation/target.
- Enqueue or upsert exactly one reconciliation job in the same durable mutation boundary or through an outbox-equivalent durable obligation.
- Scheduler startup recovery must also detect any obligation without a runnable/terminal job.
- Job handler remains idempotent.

### Penanggulangan

- Rename active profile, verify immediate job existence.
- Kill process after DB rename commit but before physical move; restart must converge.
- Repeated enqueue must not duplicate movement.
- Target collision must move state to NeedsAttention without losing the obligation.
- SOURCE-CLOSED requires zero durable rename obligations with no recovery/enqueue path.

### Verification

End-to-end rename → job → PathReconciler → restart convergence.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `src/Neuterradise.Runtime/Profiles/ProfileOperations.cs`; `src/Neuterradise.Runtime/SystemServices/Jobs/ReconciliationJobAuthority.cs`; `src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs`.  
**Traceability checked:** Profile rename mutation → persisted reconciliation operation/path state → transaction-owned deterministic job row → scheduler startup backfill → `ProfileRenameReconciliationJobHandler`. Production `ProfileDetailViewModel` no longer needs to be the authority for enqueue correctness.  
**Root-cause correction:** A rename obligation and its reconciliation job are committed atomically in the same catalog transaction; startup also backfills historical PENDING/NEEDS_ATTENTION obligations whose deterministic job row is missing.  
**Regression guard:** The durable job id equals the reconciliation operation id, insertion is mandatory before mutation commit, and scheduler startup repairs only missing historical obligations before normal interrupted-job reconciliation.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; job execution/restart remains R8.  
**Dependency findings checked:** X17, X19, X21, X24, X52, X53, X54  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Scheduler/restart execution remains R8 evidence.

## X05 — OWNER relocation obligations lack guaranteed production enqueue

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

OWNER reassignment can require a managed asset relocation, and OwnerRelocationJobHandler exists, but obligation creation and production enqueue are not one guaranteed durable path.

### Root cause / failure path

Database ownership may change before the filesystem placement catches up, with no guaranteed live job to reconcile it.

### Penyelesaian

Apply the same durable-obligation rule as X04:

- persist relocation target and expected content authority;
- enqueue/upsert the reconciliation job atomically or via a durable outbox;
- recover missing jobs at startup;
- keep the handler idempotent and collision-aware.

### Penanggulangan

Regression matrix:

- OWNER change with simple file.
- OWNER change with package/components.
- crash after DB ownership commit.
- crash after physical movement before checkpoint.
- duplicate job delivery.
- collision and NeedsAttention path.

### Verification

Database OWNER and final managed path must converge after every injected crash boundary.

---

### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `src/Neuterradise.Runtime/Media/MediaOperations.cs`; `src/Neuterradise.Runtime/SystemServices/Jobs/ReconciliationJobAuthority.cs`; `src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs`.  
**Traceability checked:** OWNER mutation → persisted relocation operation/path state → transaction-owned deterministic job row → scheduler startup backfill → `OwnerRelocationJobHandler`.  
**Root-cause correction:** OWNER relocation can no longer commit a durable relocation obligation without also committing its scheduler job; post-commit signaling is only a wake-up optimization, not correctness authority.  
**Regression guard:** Job id is the reconciliation operation id, transaction insertion is mandatory, and startup backfill restores missing historical ACTIVE-asset PENDING/NEEDS_ATTENTION obligations.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; relocation/restart execution remains R8.  
**Dependency findings checked:** X17, X19, X21, X52, X53, X54  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Scheduler/relocation execution remains R8 evidence.

# 7. Stage 3 — Database / Persistence / Lifecycle Findings

## X06 — Asset Trash fails when APPEARS or MANUAL relations remain

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Asset trash transition historically resolves OWNER but can leave APPEARS or MANUAL profile_assets relations. Database invariant trg_assets_nonactive_has_no_relations rejects movement out of ACTIVE while any blocking relation remains.

The filesystem move can occur before the DB state transition, producing a split state: bytes in Trash while DB still says ACTIVE.

### Root cause / failure path

Trash logic models ownership, not the complete relation graph.

ACTIVE asset + APPEARS/MANUAL → physical Trash move → remove OWNER only → DB ACTIVE→TRASHED update → trigger aborts → filesystem and DB diverge.

### Penyelesaian

Implement relation-aware durable Asset Trash.

Before terminal marker:

- resolve and snapshot OWNER, APPEARS, MANUAL, Hero/appearance references, and any other blocking references;
- persist enough provenance for deterministic restore;
- perform state transitions under a durable operation/checkpoint model;
- make physical movement and DB commit restart-convergent;
- restore all semantically restorable relationships symmetrically.

### Penanggulangan

Required matrix:

- OWNER only.
- APPEARS only.
- MANUAL only.
- OWNER + APPEARS.
- OWNER + MANUAL.
- all three.
- crash before move, after move, before relation clear, before state marker, after marker.
- restore after every successful trash variant.

Invariant after terminal Trash: no active relation violates trg_assets_nonactive_has_no_relations.

### Verification

Fault-injected Trash/Restore plus restart on every durable boundary.


### R3 implementation closure record — 2026-09-19

**Implementation SHA:** `0e0aa2bc2c4218146f3a548bc1175904efe68220` (source implementation chain: `c82ab6997dbdd9155bff41acdfcad3b5a465eae4` → `0b8d6e1ef14152107db1e21c7c4aecd12d548454` → `0e0aa2bc2c4218146f3a548bc1175904efe68220`)  
**Primary remediation phase:** R3  
**Changed paths:** `src/Neuterradise.Runtime/Trash/TrashPlan.cs`; `src/Neuterradise.Runtime/Trash/TrashCoordinator.cs`; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0014_r3_trash_profile_lifecycle.sql`.  
**Traceability checked:** original Section 4B targets plus the live R2 reservation/publication authorities and recovery callers on the closing SHA.  
**Root-cause correction:** Asset Trash now snapshots the complete `profile_assets` relation graph (OWNER/APPEARS/MANUAL, provenance, publication attribution) and Hero references inside the terminal catalog transaction, clears every blocking relation before `ACTIVE→TRASHED`, persists that exact restore provenance in the Trash plan, and restores semantically available relations/Hero slots after the Asset is reactivated. R2 Trash reservations remain the admission authority; R3 adds mutation/Hero guards so the snapshot cannot gain new authority during the physical/DB gap.  
**Regression guard:** Database triggers remain enabled; R3 migration blocks relation mutation and new Hero acquisition while Asset Trash is PENDING/EXECUTING, while the terminal transaction deletes all remaining relations before the existing non-ACTIVE relation trigger can fire.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/authority/recovery trace completed; the fault-injection/restart matrix remains R8 executable evidence under AGENTS.md.  
**Dependency findings checked:** X05, X17, X69, X73  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable crash/restart and FK-on matrices remain R8 evidence.

---

## X07 — NORMAL Profile Trash violates active identity/relation invariants

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

NORMAL Profile Trash can attempt to set trashed_at_ms while an active identity or APPEARS/MANUAL relationships still exist. Database trigger trg_profiles_trash_has_no_active_relations rejects that state.

Manifest/filesystem movement may already have occurred, creating divergence.

### Root cause / failure path

The Profile Trash lifecycle handles owned assets but does not atomically close the complete Profile authority graph before the terminal marker.

### Penyelesaian

Create one Profile Trash lifecycle transaction/state machine:

1. resolve disposition of owned assets;
2. snapshot active identity and relationships;
3. deactivate/archive identity as required;
4. resolve APPEARS/MANUAL and appearance references;
5. checkpoint durable physical movement;
6. set trashed marker only after invariant closure;
7. provide the exact paired restore lifecycle.

### Penanggulangan

Test NORMAL Profile with:

- active identity only;
- APPEARS;
- MANUAL;
- mixed relations;
- Hero references;
- assets reassigned during preparation;
- crash after manifest movement;
- restore after each allowed disposition.

Terminal Trash must prove no active identity/relation authority remains.

### Verification

DB trigger remains enabled. Tests must pass with the real invariant, not by disabling the trigger.


### R3 implementation closure record — 2026-09-19

**Implementation SHA:** `0e0aa2bc2c4218146f3a548bc1175904efe68220` (source implementation chain: `c82ab6997dbdd9155bff41acdfcad3b5a465eae4` → `0b8d6e1ef14152107db1e21c7c4aecd12d548454` → `0e0aa2bc2c4218146f3a548bc1175904efe68220`)  
**Primary remediation phase:** R3  
**Changed paths:** `src/Neuterradise.Runtime/Trash/TrashPlan.cs`; `src/Neuterradise.Runtime/Trash/TrashCoordinator.cs`; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0014_r3_trash_profile_lifecycle.sql`.  
**Traceability checked:** original Section 4B targets plus the live R2 reservation/publication authorities and recovery callers on the closing SHA.  
**Root-cause correction:** Profile Trash now durably records the chosen owned-Asset dispositions before executing them, snapshots all remaining Profile relations plus the active identity before the terminal marker, removes those relations, retires the active identity, and only then writes `trashed_at_ms`. Restore first reactivates the Profile, then reconstructs still-valid APPEARS/MANUAL relations and the prior active identity from durable provenance.  
**Regression guard:** R3 migration prevents new/retained relation mutations and new active identities from entering a Profile while its Trash entry is PENDING/EXECUTING, but deliberately permits OWNER movement away from the source Profile so the lifecycle can resolve dispositions. Existing DB invariants remain enabled.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/authority/recovery trace completed; the fault-injection/restart matrix remains R8 executable evidence under AGENTS.md.  
**Dependency findings checked:** X06, X08, X04, X05  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable crash/restart and FK-on matrices remain R8 evidence.


### R3 corrective revalidation record — 2026-09-19

**Corrective source SHA:** `d6063d8392bfbe522747f92a43767a71566b8cf6` (corrective chain: `5648fccbcaa8953e0601b2d9c17511c4321d14ef` → `d6063d8392bfbe522747f92a43767a71566b8cf6`)  
**Why X07 is affected by the corrective pass:** X07 requires an exact paired Profile Restore lifecycle, not only a valid Trash marker. The initial R3 closure fixed relation/identity shutdown and symmetric reconstruction, but the subsequent review found that Profile Restore still shared `IN_TRASH` authority with Purge, had no durable post-DB manifest-finalization phase, and did not validate restored manifest identity on replay. Those defects could invalidate the paired lifecycle even though the Trash-side invariant closure itself was correct.  
**Corrective lifecycle:** Profile Restore now owns explicit durable phases `RESTORE_EXECUTING → RESTORE_FINALIZING → RESTORED`. Restore cannot start while a Profile Purge authorization is active, and migration `0015_r3_restore_purge_exclusion.sql` prevents Profile Purge from starting or advancing while Restore owns the same Profile recovery material. Catalog relation/identity reconstruction commits only under `RESTORE_EXECUTING`; manifest regeneration completes under `RESTORE_FINALIZING`; terminal `RESTORED` is written only after manifest convergence.  
**Replay/legacy guard:** destination `profile.json` is validated against the expected ProfileId during replay; true source+destination absence is NeedsAttention rather than false success. Legacy R3 entries that already persisted a Restore checkpoint while still `IN_TRASH` are promoted into the corrective restore state machine before replay.  
**Verification result:** static Profile authority/restore-state/migration trace completed. DB-trigger/FK and crash/restart executable evidence remains R8.  
**Source status:** SOURCE-CLOSED after corrective revalidation  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** only R8 executable trigger/FK and crash/restart evidence remains.

---

## X08 — Profile Trash/Restore is not restart-convergent

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Profile-level Trash/Restore lacks a complete durable EXECUTING/checkpoint/resume model comparable to the physical work it performs. Process death can leave filesystem and DB at different lifecycle phases.

### Root cause / failure path

A multi-step Profile lifecycle is treated too much like one request rather than a recoverable state machine.

### Penyelesaian

Persist an idempotent Profile Trash/Restore operation with explicit phases and checkpoints.

Recovery must answer:

- what operation was intended;
- what physical steps completed;
- what DB graph was already transformed;
- whether to continue, restore, or block for attention;
- whether replaying a step is safe.

### Penanggulangan

Crash-inject at every boundary and restart repeatedly. Reconciliation must converge to exactly one terminal state without double-move, lost relation provenance, or false success.

### Verification

Automated restart matrix for Profile Trash and Profile Restore.


### R3 implementation closure record — 2026-09-19

**Implementation SHA:** `0e0aa2bc2c4218146f3a548bc1175904efe68220` (source implementation chain: `c82ab6997dbdd9155bff41acdfcad3b5a465eae4` → `0b8d6e1ef14152107db1e21c7c4aecd12d548454` → `0e0aa2bc2c4218146f3a548bc1175904efe68220`)  
**Primary remediation phase:** R3  
**Changed paths:** `src/Neuterradise.Runtime/Trash/TrashPlan.cs`; `src/Neuterradise.Runtime/Trash/TrashCoordinator.cs`; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs`; `src/Neuterradise.Runtime/SystemServices/Recovery/TrashRecovery.cs`.  
**Traceability checked:** original Section 4B targets plus the live R2 reservation/publication authorities and recovery callers on the closing SHA.  
**Root-cause correction:** Profile Trash now transitions to durable EXECUTING with persisted dispositions before any owned-Asset work, checkpoints the Profile recovery-manifest destination before the terminal marker, and Profile Restore persists its recovery/target checkpoint before physical restore. `TrashRecovery` now resumes interrupted Profile Trash and Profile Restore in addition to the pre-existing Asset/Purge branches, so restart has explicit intent and replay state instead of silently ignoring Profile entries.  
**Regression guard:** Recovery refuses to guess historical executing Profile dispositions that were never durably recorded; such legacy ambiguity becomes typed NeedsAttention rather than false success. Idempotent child Asset Trash/owner-transfer behavior is reused during Profile replay.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/authority/recovery trace completed; the fault-injection/restart matrix remains R8 executable evidence under AGENTS.md.  
**Dependency findings checked:** X06, X07, X22, X23  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable crash/restart and FK-on matrices remain R8 evidence.


### R3 corrective revalidation record — 2026-09-19

**Corrective source SHA:** `d6063d8392bfbe522747f92a43767a71566b8cf6` (corrective chain: `5648fccbcaa8953e0601b2d9c17511c4321d14ef` → `d6063d8392bfbe522747f92a43767a71566b8cf6`)  
**Review defect corrected:** Profile Restore and Profile Purge could previously overlap because Restore retained `IN_TRASH` while a restore checkpoint was active; post-DB manifest refresh also was not a durable recovery phase; Restore replay did not validate the destination manifest identity.  
**Root-cause correction:** Profile Restore is now an explicit durable state machine: `IN_TRASH → RESTORE_EXECUTING → RESTORE_FINALIZING → RESTORED`. Physical restore requires verified recovery/destination manifest evidence, catalog authority commits only from `RESTORE_EXECUTING`, manifest regeneration occurs while `RESTORE_FINALIZING`, and only successful manifest convergence writes terminal `RESTORED`. Startup `TrashRecovery` scans and resumes both restore states. Legacy R3 entries that already contain a Restore checkpoint while still `IN_TRASH` are upgraded into `RESTORE_EXECUTING` before replay.  
**Mutual exclusion:** migration `0015_r3_restore_purge_exclusion.sql` and the Restore-side active-Purge check make Profile Restore/Purge mutually exclusive. A stale Purge preparation that loses the race returns a typed conflict instead of surfacing a raw SQLite constraint error.  
**Manifest replay guard:** `ManagedMoveExecutor.ExecuteProfileManifestRestoreMoveAsync` now validates ProfileId on the destination manifest; true source+destination absence is NeedsAttention and cannot silently advance catalog authority.  
**Verification result:** static source/state-machine/recovery trace completed. Executable crash injection and restart matrix remain R8 evidence.  
**Source status:** SOURCE-CLOSED after corrective revalidation  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** only R8 executable evidence remains.

---

# 8. Stage 4 — Import / Media / Vault / Storage Findings

## X09 — Same-volume replay misclassifies an operation-owned target as UnexpectedTarget

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

After a crash, a same-volume move may already have placed the correct bytes at the planned final target while the durable checkpoint is behind. Replay can treat that existing target as an unexpected collision instead of recognizing completed work owned by the same operation.

### Root cause / failure path

Recovery distinguishes only target exists versus target absent, not collision versus valid idempotent replay target.

### Penyelesaian

For an existing target, resolve target authority using:

- persisted operation/asset ID;
- planned relative path;
- expected length and hash;
- component/package authority where applicable.

If the target is exactly the operation-owned expected object, checkpoint the completed phase and continue. If authority differs, return collision/NeedsAttention.

### Penanggulangan

Crash at each same-volume move/checkpoint boundary for COPY and MOVE; restart must converge without false collision or duplicate bytes.

### Verification

Repeat replay multiple times and assert idempotence.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 753ba292d3ef4317658dcb9ddaebeb7eedd58cca  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Storage/ManagedMoveExecutor.cs`  
**Traceability checked:** persisted same-volume move plan → `ResolveTargetAuthorityAsync` → existing-target byte verification → placement checkpoint; cross-volume target-race replay follows the same verification-before-convergence rule.  
**Root-cause correction:** an existing target is no longer classified as `UnexpectedTarget` merely because the external source still exists. When durable target authority belongs to the same Asset, replay verifies exact bytes and checkpoints the already-published target; another Asset remains a collision.  
**Regression guard:** same-Asset replay converges only after expected length/SHA verification; other-Asset authority or byte mismatch remains fail-closed.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; crash-injection execution remains R8.  
**Dependency findings checked:** X12, X17  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable same-volume/cross-volume crash matrix remains R8 evidence.


---

## X10 — MOVE source deletion is fail-open when stable object identity is unavailable

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

MOVE cleanup can reach deletion using a pathname after verification. If stable source identity is absent or the pathname is replaced between verification and deletion, the object deleted may not be the object that was verified.

### Root cause / failure path

Content verification and destructive deletion are not guaranteed to bind to the same filesystem object.

### Penyelesaian

Fail closed.

- Require stable identity adequate for destructive cleanup.
- Bind delete semantics to the verified object/handle where the platform permits.
- Revalidate identity immediately before destructive action.
- If identity cannot be proven, preserve source and report cleanup attention rather than claiming MOVE completion.
- COPY semantics are safer than deleting an uncertain source.

### Penanggulangan

- null identity;
- renamed source;
- pathname replacement race;
- same-size same-hash replacement where identity differs;
- access denied;
- restart while SourceDeletePending.

Never delete an unproven object.

### Verification

Adversarial rename/replace tests against the actual Windows cleanup implementation.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 8b12069f227dbd1dcf91397cfdaba30c7d112722  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Storage/SourceIdentityHelper.cs`; `src/Neuterradise.Runtime/SystemServices/Storage/SourceCleanupExecutor.cs`  
**Traceability checked:** intake identity capture → persisted identity JSON → cleanup open handle → content verification → Windows volume/file-index verification on that handle → handle-bound delete disposition.  
**Root-cause correction:** MOVE deletion now requires stable Windows file-object identity. Path/timestamp/hash equality alone cannot authorize deletion, and the verified open handle is the object marked for deletion. Missing identity preserves source.  
**Regression guard:** null identity, pathname replacement, object-identity mismatch, hash/length mismatch, Vault-contained source, access denial, and locking all prevent destructive cleanup.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; adversarial filesystem execution remains R8.  
**Dependency findings checked:** X11, X14  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Windows rename/replace race execution remains R8 evidence.



### R2 corrective closure record — 2026-09-19

**Reopen cause:** post-implementation review proved that the verified handle had only read access; `FILE_SHARE_DELETE` permits sharing but does not grant Windows `DELETE` access, so handle-bound deletion could fail even after identity verification.  
**Corrective SHAs:** `4915d446219a44573fd03f0ee8ad163d422e2bf7`; `4d90b70bedea340b063c0b26f3bb1102be1ff0e5`  
**Corrective paths:** `SourceIdentityHelper.cs`; `SourceCleanupExecutor.cs`  
**Corrective authority:** destructive cleanup now opens the exact source object with `GENERIC_READ | DELETE`, permits read/delete sharing but excludes concurrent writers, hashes and validates stable file identity on that same handle, then applies `FileDispositionInfo` to that handle. Missing DELETE authority, identity mismatch, write-sharing conflict, replacement, or content drift fails closed and preserves source.  
**Revalidation:** the prior pathname/object race is closed at source level; X11 package cleanup consumes the same corrected primitive.  
**Source status:** SOURCE-CLOSED (CORRECTED)  
**Runtime status:** NOT-YET-VERIFIED — adversarial Windows rename/replace/delete execution remains R8 evidence.


---

## X11 — REUSE package cleanup reads component authority from the reused target instead of the candidate source

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

`SourceCleanupExecutor` correctly states that cleanup-source component authority is the CandidateAsset, but for multi-file cleanup it loads components using `targetAssetId = ManagedAssetId`. Under REUSE, `ManagedAssetId` is the pre-existing reused library asset, while `sourceAssetId` is the newly imported candidate.

Relevant path:

- `src/Neuterradise.Runtime/SystemServices/Storage/SourceCleanupExecutor.cs`

The executor then derives source paths from those reused-target component records and writes cleanup states back against the candidate asset ID.

### Root cause / failure path

REUSE has two different authorities:

- **candidate** = current external source that may be deleted under MOVE;
- **reused target** = existing managed library asset that proves durable destination authority.

The code reads component membership/source metadata from the target while destructive cleanup must be authorized from the candidate.

candidate package A → duplicate decision REUSE library package B → cleanup loads B's component records → candidate cleanup can target stale/unrelated source component paths or fail to match candidate component rows.

### Penyelesaian

Split cleanup authority explicitly:

- candidate component set supplies OriginalSourcePath, SourceIdentityJson, source hash/length, and cleanup state;
- reused target component set supplies managed-destination verification only;
- pair components through canonical normalized component identity/bundle authority;
- refuse destructive cleanup if the two sets cannot be reconciled deterministically.

### Penanggulangan

Regression matrix:

- included package;
- reused package from a different historical source directory;
- same bundle with different original paths;
- candidate package with missing/changed component;
- retry/restart after partial component cleanup.

### Verification

For REUSE+MOVE, every deleted external path must originate from the current candidate's persisted component authority, never from the reused library asset's historical source metadata.

### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 8b12069f227dbd1dcf91397cfdaba30c7d112722  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Storage/SourceCleanupExecutor.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportWrites.cs`  
**Traceability checked:** ImportItem CandidateAssetId/ReusedAssetId → candidate component authority → normalized component reconciliation with managed/reused target → per-component managed verification → candidate-source cleanup state.  
**Root-cause correction:** REUSE cleanup obtains destructive source paths/identities only from the current Candidate. The reused Asset supplies managed-destination verification only. Candidate and target component sets must reconcile by normalized path, length, and SHA before deletion.  
**Regression guard:** incomplete component authority, component-set mismatch, Vault-owned source member, invalid managed path, or byte mismatch preserves source/fails closed.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; package MOVE/REUSE execution remains R8.  
**Dependency findings checked:** X10, X13, X14, X72  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable multi-component cleanup matrix remains R8 evidence.

---

## X12 — Profile-folder filesystem side effects occur before DestinationPrepared is durably checkpointed

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

`ImportCommitCoordinator.PrepareDestinationAsync` creates the canonical profile directory and writes `profile.json` inside `ProvisionProfileFolderAsync`. The caller advances the import operation to `DestinationPrepared` only **after** `PrepareDestinationAsync` returns.

Relevant path:

- `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs`

### Root cause / failure path

The operation persists some destination identity early, but the physical folder/manifest mutation itself has no dedicated durable checkpoint before it occurs.

persist destination IDs → create profile folder → write manifest → process/cancellation/failure before DestinationPrepared checkpoint → durable operation checkpoint lags filesystem state.

Replay may be able to converge in many cases because names are deterministic, but the source does not make that side effect/checkpoint relationship explicit enough to guarantee cleanup/recovery for every interruption.

### Penyelesaian

Make folder provisioning an explicit idempotent durable phase:

- persist the intended profile folder path before mutation;
- checkpoint provisioning intent;
- create/verify directory and manifest;
- checkpoint physical completion;
- make replay distinguish owned already-completed material from collision;
- make cancellation rollback/recovery consume the same phase.

### Penanggulangan

Crash/cancel injection before directory creation, after directory creation, after manifest write, and before DestinationPrepared checkpoint.

### Verification

Every injected interruption must restart to either a valid resumed DestinationPrepared state or a deterministic rollback without orphan profile material.

### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 753ba292d3ef4317658dcb9ddaebeb7eedd58cca  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs`  
**Traceability checked:** destination Profile/token allocation → persisted profile-folder target in CommitState → provisioning-intent persistence → filesystem creation/manifest write → provisioning completion → DestinationPrepared progression.  
**Root-cause correction:** Profile-folder mutation now has a durable replayable target and explicit provisioning intent before physical side effects. Re-entry uses persisted folder authority and rejects a valid manifest owned by another Profile.  
**Regression guard:** retry cannot silently allocate a different folder; owned existing material converges, conflicting ownership blocks instead of overwriting.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; kill-boundary execution remains R8.  
**Dependency findings checked:** X09, X17  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable provisioning crash matrix remains R8 evidence.


### R2 corrective closure record — 2026-09-19

**Reopen cause:** an already-existing final folder with missing/malformed manifest could still be adopted, and manifest-write failure/cancellation was returned as a status rather than necessarily throwing before publication.  
**Corrective SHAs:** `b8e2721f103efe1c9de47f9229f489bf4fa450bc`; `45f226f47c7e1ff924248a43108d52a61733acda`  
**Corrective path:** `ImportCommitCoordinator.cs`  
**Corrective authority:** provisioning uses an operation-scoped `.<operationId>.provisioning` directory, writes and verifies `profile.json` there, requires a successful manifest-write result, honors cancellation before publication, and atomically renames staging to the persisted final folder. An existing final folder is accepted only when a valid manifest names the same Profile; a concurrent winner is revalidated the same way.  
**Revalidation:** a foreign empty/malformed directory can no longer become owned merely because its path matches the persisted plan, while crash replay retains an operation-owned staging artifact that can safely converge.  
**Source status:** SOURCE-CLOSED (CORRECTED)  
**Runtime status:** NOT-YET-VERIFIED — kill-boundary filesystem execution remains R8 evidence.


---

## X13 — Health/integrity evaluation is package-unaware

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

An asset that is represented by multiple managed components can appear healthy when only the primary file is checked while required package members are missing, mismatched, or displaced.

### Root cause / failure path

Health is modeled at asset-primary-file level while the authoritative object may be a component set.

### Penyelesaian

Define package-aware health:

- enumerate required components from durable authority;
- verify each component path, length, hash, and containment;
- aggregate to asset health;
- distinguish optional derived artifacts from required source package members.

### Penanggulangan

Negative tests remove, alter, rename, or move one component at a time. Asset health must degrade deterministically.

### Verification

Health check against real multi-component assets after restart.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 9c2e79edbfa7744dd02f7e1032dd2c1b01fc7bb4  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/Maintenance/LibraryHealthEvaluator.cs`  
**Traceability checked:** ACTIVE Model Asset → durable `asset_components` → canonical package directory → each component containment/path → existence → length → Deep-mode SHA aggregation.  
**Root-cause correction:** health no longer treats a multi-component model as one primary file. Every required durable package member is evaluated and any missing/displaced/mismatched member degrades the owning Asset.  
**Regression guard:** invalid containment, missing file, length mismatch, and Deep SHA mismatch emit deterministic package findings.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; real-package restart scan remains R8.  
**Dependency findings checked:** X11, X14, X17  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable package-health matrix remains R8 evidence.


---

## X14 — Dependency discovery can fail open

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

If dependency discovery for a media/package type fails or is incomplete, the asset can proceed without proving whether required dependent files exist.

### Root cause / failure path

Discovery failure is treated as no dependencies instead of unknown dependency state.

### Penyelesaian

Introduce an explicit dependency discovery state:

- Complete;
- MissingDependencies;
- FailedRetryable;
- FailedTerminal / Unsupported;
- Unknown.

Unknown or failed discovery may not silently become dependency-free.

### Penanggulangan

Parser errors, permission failures, malformed package metadata, unsupported references, and missing sidecars must all have deterministic admission behavior.

### Verification

Known dependency fixtures with intentionally broken discovery.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 2eded5f62b403b33b4fc2d4bf05738fc739fcf2b  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0011_r2_import_durability_authority.sql`; `src/Neuterradise.Runtime/Media/Model/ModelPackageContracts.cs`; `src/Neuterradise.Runtime/SystemServices/Database/DbEnum.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/AssetWrites.cs`; `src/Neuterradise.Runtime/Import/Preparation/ImportPreparationCoordinator.cs`  
**Traceability checked:** model discovery/parser → explicit discovery state → persisted `assets.dependency_discovery_state` plus dependency status/bundle hash → cleanup/verification consumers.  
**Root-cause correction:** parser I/O/format failures and unsupported discovery can no longer collapse into SELF_CONTAINED. Discovery truth is persisted separately as COMPLETE/MISSING/UNKNOWN/FAILED_RETRYABLE/FAILED_TERMINAL/UNSUPPORTED; bundle authority exists only after complete discovery.  
**Regression guard:** malformed discovery, inaccessible input, unsupported package discovery, missing dependencies, and complete packages remain distinguishable after restart.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; parser fixture execution remains R8.  
**Dependency findings checked:** X10, X11, X13, X15, X16  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable parser/package fixture matrix remains R8 evidence.



### R2 corrective closure record — 2026-09-19

**Reopen cause:** `AddSafeUri` returned `false` for unsafe/remote references and GLTF/DAE callers could silently interpret the omitted reference set as dependency-free.  
**Corrective SHA:** `4915d446219a44573fd03f0ee8ad163d422e2bf7`  
**Corrective path:** `ModelPackageContracts.cs`  
**Corrective authority:** embedded `data:` URIs remain explicitly self-contained; remote HTTP(S) references produce `UNSUPPORTED`; rooted/traversal/invalid package references produce terminal discovery failure. Rejected external references therefore cannot disappear into `SELF_CONTAINED + COMPLETE`.  
**Revalidation:** complete/self-contained authority is emitted only after every non-embedded dependency reference is accepted into canonical discovery.  
**Source status:** SOURCE-CLOSED (CORRECTED)  
**Runtime status:** NOT-YET-VERIFIED — malformed/remote/traversal fixtures remain R8 evidence.


---

## X15 — Duplicate/reuse package authority split

**Status:** REJECTED-AS-INDEPENDENT-FINDING

### Temuan audit sebelumnya

The earlier ledger treated package duplicate/reuse identity as independently unsafe because a model candidate can have more than one component.

### Falsification result

This is **not retained as an independent defect**.

The current source computes `ModelPackageDiscovery.BundleSha256` for complete/self-contained packages and passes that value into `ExactDuplicateDetector.DetectDuplicateAsync`. The duplicate detector uses `bundle_sha256` when that authority exists. Missing/unknown dependency states are separately persisted and are validated by the verification path.

Relevant paths:

- `src/Neuterradise.Runtime/Import/Preparation/ImportPreparationCoordinator.cs`
- `src/Neuterradise.Runtime/Import/Preparation/ExactDuplicateDetector.cs`
- `src/Neuterradise.Runtime/Import/Verification/VerificationValidator.cs`

### Canonical disposition

Do **not** implement X15 as a separate fix.

Remaining real risks are already owned by:

- **X14** — dependency discovery can fail open;
- **X72** — a previously accepted REUSE decision is not revalidated at the final commit boundary.

### Penanggulangan

Regression coverage for package duplicate/reuse belongs to X14 and X72. If a future failure demonstrates a distinct package-identity root cause not covered by those findings, allocate X74+ with direct source evidence.

### Verification

The trusted ledger must exclude X15 from source-remediation counts and implementation queues.

## X16 — Missing-dependency acknowledgement is already durable and executable

**Status:** REJECTED-AS-INDEPENDENT-FINDING

### Temuan audit sebelumnya

The earlier ledger claimed that acknowledgement of missing model dependencies was presentation-only and was not consumed by durable verification.

### Falsification result

That claim is false for the audited source.

`VerificationDraftV1` persists `AcknowledgedMissingDependencyItemIds`, and `VerificationValidator` explicitly consumes that durable set when `dependency_status = DependenciesMissing`. For MOVE policy, `DependenciesUnknown` is separately blocked.

Relevant path:

- `src/Neuterradise.Runtime/Import/Verification/VerificationValidator.cs`

The source therefore already has an executable durable acknowledgement contract.

### Canonical disposition

No X16 remediation is authorized.

Dependency correctness remains covered by X14, and source cleanup fail-closed behavior for missing/unknown dependencies remains covered by the storage path.

### Penanggulangan

Keep regression coverage proving that acknowledgement survives reload/restart and that unknown dependencies remain prohibited under MOVE.

### Verification

X16 is excluded from remediation counts and must never be reintroduced unless new source evidence proves the durable acknowledgement is bypassed.

## X17 — Some placement paths bypass the collision-safe allocator

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ManagedPathPlanner contains explicit collision allocation behavior, but every package/profile/asset placement path must use it. Direct construction of a planned path can bypass conflict allocation and produce avoidable TargetCollision/NeedsAttention.

### Root cause / failure path

Path generation and collision allocation are separable APIs and callers can select the weaker one.

### Penyelesaian

Make collision-safe allocation the canonical public placement API for any new managed physical authority. Internal raw planning may remain only where the caller immediately performs authoritative allocation.

### Penanggulangan

- same human-readable names;
- same extension;
- Unicode normalization variants;
- case-insensitive collision;
- package directories;
- Hero artifacts.

### Verification

No production placement caller may materialize a path without collision authority.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 753ba292d3ef4317658dcb9ddaebeb7eedd58cca; 9c2e79edbfa7744dd02f7e1032dd2c1b01fc7bb4; b7f403d27950b5e1ad3262b58d8e3ab611ebf242; 847049101982640d9c1b034fb63eb1e608e0beb7; 288c78be05513302bc9e634f5026130f80e22652  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Storage/ManagedPathPlanner.cs`; `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs`; `src/Neuterradise.Runtime/Media/MediaOperations.cs`; `src/Neuterradise.Runtime/Trash/RestoreExecutor.cs`; `src/Neuterradise.Runtime/Profiles/ProfileOperations.cs`; `src/Neuterradise.Runtime/Profiles/UnknownResolutionOperations.cs`; `src/Neuterradise.Runtime/SystemServices/Storage/ProfileManifestWriter.cs`  
**Traceability checked:** new Profile folder, imported simple Asset, model package, owner relocation, restore, profile rename, Unknown-resolution relocation, and manifest-repair paths. Raw planning remains prediction/inspection-only where no new physical authority is allocated.  
**Root-cause correction:** collision-safe allocators now exist for Profile, Asset, model package, and Hero identities, and R2 physical-authority creation/relocation callers use allocator/occupied-path authority instead of raw deterministic construction. Manifest repair remains separate from placement allocation.  
**Regression guard:** occupied catalog current/target paths plus existing filesystem entries force deterministic suffix expansion; another entity's existing path is never selected merely because the human-readable name matches.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/caller trace completed; collision execution remains R8.  
**Dependency findings checked:** X04, X05, X09, X12, X13  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable Unicode/case/package/Hero collision matrix remains R8 evidence.


---

## X18 — Manifest-derived path containment weakness was not proven

**Status:** REJECTED-AS-INDEPENDENT-FINDING

### Temuan audit sebelumnya

The earlier ledger generalized that manifest/recovery path handling could be vulnerable if it relied on textual prefix checks.

### Falsification result

No concrete production path supporting that claim was found in the audited source.

The inspected update/storage/recovery consumers use `RootPathRules.ResolveContainedPath`, `Path.GetFullPath`, root equality/containment helpers, and reparse-point checks at destructive boundaries. A hypothetical warning about naive `StartsWith` containment is not a source defect without a reachable production path using it as authority.

### Canonical disposition

X18 is rejected as an independent source finding.

This does **not** remove archive-extraction hardening X64, whose scope is narrower: explicit extraction-boundary proof/static-analysis clarity while preserving the existing containment defenses.

### Penanggulangan

Any future path-containment finding must name the exact producer, consumer, unsafe comparison, and escaping input.

### Verification

X18 is absent from remediation queues and source-defect counts.

# 9. Stage 5 — Jobs / Scheduler / Concurrency / Recovery Findings

## X19 — Pause intent can become durable CANCELLED

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

A running job interrupted by Pause can surface handler outcome Cancelled and be durably classified as CANCELLED instead of PAUSED.

### Root cause / failure path

CancellationToken cancellation is used as both mechanism and semantic intent. The scheduler does not always preserve the reason the CTS was triggered.

### Penyelesaian

Scheduler must be the authority that maps control intent to terminal/nonterminal job state.

Track explicit JobControlIntent at interruption:

- Pause → PAUSED/reclaimable;
- Cancel → CANCELLED;
- Shutdown → resumable interrupted state;
- timeout/failure → normal failure policy.

Handlers report safe-boundary interruption; scheduler decides durable semantics.

### Penanggulangan

Race matrix Pause/Cancel/Resume/Shutdown during queued, running, checkpointing, and completion transitions.

### Verification

Pause can never permanently cancel resumable work.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `7f88baa130e108868ac9ed257f0f1e106206e6c7` (foundation `36d1104d347e0312906d3832ecdacd9cd0d96fe8`)  
**Primary remediation phase:** R4  
**Changed paths:** `JobScheduler.cs`; `JobCancellationOperations.cs`; `JobLeaseStore.cs`; `JobWrites.cs`.  
**Traceability checked:** handlers returning Cancelled, registration lifetime, normal completion, Pause/Cancel intent, scheduler shutdown and catch ordering.  
**Root-cause correction:** scheduler now owns semantic classification. Pause becomes PAUSED, explicit Cancel becomes CANCELLED, and Shutdown returns interrupted RUNNING work to resumable PENDING with interrupted-attempt refund. Handler cancellation results are inputs, not durable semantic authority.  
**Regression guard:** intent survives until completion; linked scheduler-token cancellation is routed through Shutdown intent, including the lease-crossing-snapshot race.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static trace complete; executable race matrix remains R8.  
**Dependency findings checked:** X20, X26, X51, X66  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 concurrency/restart evidence only.

---

## X20 — ImportFinalizer is outside Pause/Cancel quiescence ownership

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ImportFinalizer performs domain and Stage 1 mutations on its own loop. Cancellation settlement can overlap with Finalizer mutation because both are not governed by one per-unit execution/mutation lease.

### Root cause / failure path

Scheduler quiescence does not cover all actors that mutate one ImportUnit.

### Penyelesaian

Introduce one per-ImportUnit mutation lease or equivalent serialization authority shared by:

- Finalizer;
- cancellation settlement;
- verification commit;
- recovery;
- control actions.

Pause/Cancel must signal all actors and wait for a safe durable boundary before settlement.

### Penanggulangan

Race tests at every Stage 1 boundary and before/after DomainAuthorityCommitted.

### Verification

No cancellation settlement can undo or race a concurrently completing finalizer phase.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `724230cbacaec9797dac3c1f5b9f2d19fc3351f5` (authority introduced `e95e66440ec97cbdd16378244e800d5b413c12e5`; actor wiring `85f322f6d2970f1e2c79f4665bd366aad58f3fa7` and `e666c7e69736ae768aab72245337e9505a2f0fd1`)  
**Primary remediation phase:** R4  
**Changed paths:** `ImportUnitMutationCoordinator.cs`; `CatalogDb.cs`; `ImportFinalizer.cs`; `ImportCancellationSettlement.cs`; `ImportUnitControlAuthority.cs`; `ImportCommitCoordinator.cs`; `ImportPublicationCoordinator.cs`; `Stage2PreparationCoordinator.cs`; `ImportRecovery.cs`.  
**Traceability checked:** Finalizer, cancellation settlement, Stage 1, Stage 2, publication, recovery and nested same-unit call chains.  
**Root-cause correction:** one Catalog-owned per-ImportUnit lease serializes logical mutation spans across DB/filesystem/checkpoint work rather than only individual SQLite transactions. Re-entry is scoped to the same async flow and same unit.  
**Regression guard:** ownership is established synchronously before a contended wait; outer lease cannot release ahead of nested leases; unrelated units remain independent.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static ownership/deadlock trace complete; contention/fault injection remains R8.  
**Dependency findings checked:** X19, X21, X24, X52, X73  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 contention/crash matrix only.

### R4 post-review corrective closure — 2026-09-19

**Corrective SHA:** `2828b2b1eee8925b021ae557705a5ed12aa218ea`  
**Review defect:** Retry still bypassed the per-ImportUnit mutation authority from `ImportViewModel`; stale control side effects could also project into Asset jobs independently of the unit transition.  
**Correction:** Retry is now routed through `ImportUnitControlAuthority.RetryAsync`, which acquires both process-wide mutation admission and `ImportUnitMutations.EnterAsync(unitId)`. Clear-history item commands use the same per-unit authority.  
**Static revalidation:** Finalizer/cancel/commit/recovery/control/Retry now share the same unit-level serialization boundary for the user-facing mutation span.  
**Source status:** SOURCE-CLOSED after corrective review  
**Runtime status:** NOT-YET-VERIFIED — R8 remains the executable contention/fault-injection authority.

---

## X21 — Lifecycle/checkpoint writes are not sufficiently monotonic against stale writers

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

A stale asynchronous writer can write a nonterminal or forward state after another actor has already made the unit terminal, for example CANCELLED → READY/COMMITTING.

### Root cause / failure path

State transitions do not consistently use compare-and-swap/row-version plus an explicit legal-transition lattice.

### Penyelesaian

Centralize state transition authority.

- Every write states expected current state/version.
- Terminal states are absorbing unless an explicit recovery transition is defined.
- Stale writes fail without side effects.
- Checkpoint writes are scoped to the operation generation that owns them.

### Penanggulangan

Concurrent stale-writer tests for Cancel, Failure, Ready, Committing, Published, and recovery.

### Verification

Database assertion: no illegal reverse/terminal-escape transition.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `e95e66440ec97cbdd16378244e800d5b413c12e5`  
**Primary remediation phase:** R4  
**Changed paths:** `ImportReadModels.cs`; `ImportWrites.cs`; `ImportFinalizer.cs`; `Stage2PreparationCoordinator.cs`; `ImportPublicationCoordinator.cs`.  
**Traceability checked:** general lifecycle writer plus Pause/Resume/Cancel/Retry predicates in `ImportUnitWrites.cs`, Finalizer, Stage-2 and publication callers.  
**Root-cause correction:** `ImportLifecycle.CanTransitionTo` is the lifecycle lattice; `ImportWrites.UpdateUnitStateAsync` reads current state/version under write serialization and commits only against the same state + row_version, with optional expected-state/version CAS. Terminal states are absorbing.  
**Regression guard:** stale/illegal transitions throw `CatalogConcurrencyConflictException`; terminal shared-interest cleanup occurs only inside a successful guarded transition.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static transition/caller trace complete; stale-writer execution remains R8.  
**Dependency findings checked:** X20, X24, X25, X69, X73  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 lifecycle-race matrix only.

### R4 post-review corrective closure — 2026-09-19

**Corrective SHA:** `2828b2b1eee8925b021ae557705a5ed12aa218ea`  
**Review defect:** `SetPausedAsync` and `ResumeUnitAsync` mutated Asset jobs even when the guarded `import_units` UPDATE changed zero rows. Retry also attempted to resume `FAILED_RETRYABLE` HashAsset jobs through `JobWrites.ResumeAsync`, whose SQL only accepts `PAUSED`.  
**Correction:** subordinate job projection executes only after a successful unit transition. Pre-Stage-1 Retry now commits `FAILED_RETRYABLE → INTAKE` and `HashAsset FAILED_RETRYABLE → PENDING` in the same Catalog transaction, clearing retry timing/error/completion fields without resetting attempt authority.  
**Static revalidation:** stale terminal/control commands are DB no-ops with no job side effects; Retry no longer depends on an incompatible PAUSED-only API.  
**Source status:** SOURCE-CLOSED after corrective review  
**Runtime status:** NOT-YET-VERIFIED — stale-writer/race execution remains R8.

---

## X22 — Shutdown timeout can release VaultLock while mutation disposal is still active

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

A bounded shutdown can time out while background writers/finalizers are still disposing or mutating. If VaultLock is released anyway, a second process may acquire the Vault concurrently.

### Root cause / failure path

Lock lifetime is not strictly greater than all possible mutation-writer lifetimes.

### Penyelesaian

VaultLock release is last.

If writers cannot be proven stopped before deadline:

- do not write a clean shutdown marker;
- preserve recoverable durable state;
- keep process/lock ownership until mutation capability is terminated, or fail shutdown conservatively rather than exposing concurrent writers.

### Penanggulangan

Slow/stuck writer fault injection. A second process must never acquire VaultLock while the first can still mutate.

### Verification

Two-process lock test around shutdown timeout.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `b11d19ac4212e6aeaf52c24aeaf0b0750110b90f`  
**Primary remediation phase:** R4  
**Changed paths:** `ShutdownCoordinator.cs`; `CatalogMutationAdmissionGate.cs`; `CatalogDb.cs`.  
**Traceability checked:** command drain, scheduler shutdown task, service disposal tasks, BootstrapContext disposal and VaultLock lifetime.  
**Root-cause correction:** timeout no longer authorizes VaultLock release while a mutation-capable task remains alive. Timed-out scheduler/disposal tasks are awaited to actual termination before context disposal.  
**Regression guard:** BootstrapContext/VaultLock release is last; faulted tasks may record failure but no still-running mutation task is abandoned behind lock release.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static lifetime trace complete; slow/hung execution remains R8.  
**Dependency findings checked:** X23, X52, X54, X55  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 shutdown timing evidence.

### R4 post-review corrective closure — 2026-09-19

**Corrective SHA:** `1668aeaf231459b97bb2651a2d4325bbbfe32265`  
**Review defect:** `JobDispatcher.StopAsync` could hit its bounded wait, signal `WorkerShutdown.Cancel()`, and return without awaiting worker task termination. That made higher-level scheduler/runtime disposal capable of completing while a mutation-capable worker was still alive.  
**Correction:** the bounded phase now decides only when cancellation is signalled. After signalling, `StopAsync` always awaits the original `Task.WhenAll(workers)` to terminal completion/fault before returning. Scheduler/runtime disposal therefore cannot retire ahead of dispatcher workers, and `ShutdownCoordinator` cannot reach `BootstrapContext.DisposeAsync`/VaultLock release while they still run.  
**Static revalidation:** `ShutdownCoordinator → ProductionRuntimeRegistry → JobScheduler → JobDispatcher` ownership is strictly nested through actual task termination.  
**Source status:** SOURCE-CLOSED after corrective review  
**Runtime status:** NOT-YET-VERIFIED — slow/non-cooperative worker timing remains R8.

---

## X23 — Session marker is not yet a complete crash protocol

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

SessionMarkerStore can persist clean/unclean state, but the lifecycle must prove that unclean is written early enough and clean is written only after all mutation authorities are closed. Otherwise marker state can misclassify a crash.

### Root cause / failure path

Marker existence is not itself a recovery protocol unless ordering relative to scheduler, DB, worker, and VaultLock is guaranteed.

### Penyelesaian

Formalize:

startup acquires authority → persist current session unclean marker → perform startup/recovery → run → close command admission → quiesce writers → flush DB/diagnostics → stop worker → release VaultLock → persist/commit clean semantics in an ordering that cannot falsely advertise safety.

The marker supplements durable operation inspection; it never replaces it.

### Penanggulangan

Crash at every startup/shutdown phase and assert recovery decision.

### Verification

A clean marker must never coexist with an unquiesced durable mutation from the same session.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `97ebefaa36dc77772e706e92246beeb0e70dc4a4`  
**Primary remediation phase:** R4  
**Changed paths:** `AppBootstrapper.cs`; `BootstrapContext.cs`; `ShutdownCoordinator.cs`; existing `SessionMarkerStore.cs` is the persistence authority.  
**Traceability checked:** VaultLock acquisition, first writable startup boundary, startup failure, Ready ownership and ordered clean shutdown.  
**Root-cause correction:** startup writes UNCLEAN immediately after writable Vault ownership and before catalog/recovery mutation. SessionId + SessionGeneration travel through BootstrapContext; shutdown writes CLEAN only for that same identity after quiescence succeeds.  
**Regression guard:** failed startup or failed/timed-out shutdown leaves non-clean evidence; shutdown cannot invent a new clean generation.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static marker ordering trace complete; crash-kill/restart remains R8.  
**Dependency findings checked:** X22, X52, X54  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 crash/restart evidence.

---

## X24 — Start/Prioritize can persist focus for a terminal or stale ImportUnit

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

`ImportUnitControlAuthority.StartAsync` calls `ResumeUnitAsync` and ignores its Boolean result, then always calls `ImportPriorityOperations.FocusAsync`. `PrioritizeAsync` calls `FocusAsync` without validating ImportUnit lifecycle.

`ResumeUnitAsync` itself refuses terminal states, but the subsequent focus mutation is not conditioned on that refusal.

Relevant paths:

- `src/Neuterradise.Runtime/Import/ImportUnitControlAuthority.cs`
- `src/Neuterradise.Runtime/SystemServices/Jobs/ImportPriorityOperations.cs`
- `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportUnitWrites.cs`

### Root cause / failure path

Lifecycle validation and scheduler-focus persistence are separate writes.

terminal/stale unit → ResumeUnitAsync returns false → caller ignores false → focused-import setting points at unit that cannot resume.

### Penyelesaian

Make Start/Prioritize one lifecycle-validated operation:

- validate current state/version;
- resume only when legal;
- persist focus only if the unit remains focus-eligible;
- clear/refuse stale focus deterministically;
- use transactional/CAS semantics where the lifecycle and focus decision must be atomic.

### Penanggulangan

Race terminal completion/cancel/failure against Start and Prioritize.

### Verification

A terminal/missing unit can never become the persisted focused import and cannot alter runnable work through a stale control command.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `85f322f6d2970f1e2c79f4665bd366aad58f3fa7`  
**Primary remediation phase:** R4  
**Changed paths:** `ImportUnitControlAuthority.cs`; `ImportPriorityOperations.cs`; `JobWrites.cs`.  
**Traceability checked:** Start/Resume/Prioritize, focused setting, lifecycle eligibility, job reprioritization and R2 shared-Asset interests.  
**Root-cause correction:** focus is persisted only for an existing, unpaused, nonterminal/pre-publication unit inside the same transaction that changes focus/priority. Start fails when focus cannot be established and stale persisted focus is cleared instead of refreshed.  
**Regression guard:** terminal/missing/paused units cannot become newly focused; shared Asset work still uses durable interest rows.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static focus/control trace complete; race execution remains R8.  
**Dependency findings checked:** X20, X21, X69  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 control-race matrix.

### R4 post-review corrective closure — 2026-09-19

**Corrective SHA:** `2828b2b1eee8925b021ae557705a5ed12aa218ea`  
**Review defect:** although stale focus was rejected, `ResumeUnitAsync` could already have changed Asset jobs before `FocusAsync` rejected a terminal/stale unit. `SetPausedAsync` had the symmetric stale side effect.  
**Correction:** Pause/Resume subordinate job updates are now conditioned on `changed > 0` from the lifecycle-eligible unit UPDATE. A stale/terminal Start/Pause command therefore cannot change job state before focus validation.  
**Static revalidation:** terminal/missing/stale units neither become focus authority nor alter runnable/paused Asset work through the stale command.  
**Source status:** SOURCE-CLOSED after corrective review  
**Runtime status:** NOT-YET-VERIFIED — Start/Pause/Prioritize race execution remains R8.

## X25 — Optional Stage 2 capabilities can incorrectly gate readiness or terminal failure

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Capabilities modeled as optional can still leak into readiness/failure closure so an optional capability failure/cancellation can prevent readiness or push the unit toward terminal failure.

### Root cause / failure path

Capability Required metadata and dependency closure/readiness evaluation are not one semantic authority.

### Penyelesaian

Required and optional capability state must be explicit throughout:

- job creation;
- dependency graph;
- terminal projection;
- readiness join;
- UI progress.

Optional failure may degrade features and surface attention but cannot block ReadyForVerification unless another truly required capability depends on it.

### Penanggulangan

Matrix for every capability with Success/FailedTerminal/Cancelled/Unavailable. Verify readiness result from Required flag, not hard-coded job type.

### Verification

Face capability unavailable must not fail an otherwise valid import when face work is optional.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `e95e66440ec97cbdd16378244e800d5b413c12e5`  
**Primary remediation phase:** R4  
**Changed paths:** `CapabilityReads.cs`; `Stage2PreparationCoordinator.cs`.  
**Traceability checked:** `CapabilityApplicability.GetRequired`, seeding, readiness aggregation, failed-asset aggregation and lifecycle projection.  
**Root-cause correction:** every eligible effective Asset remains in the denominator, but only the canonical Required capability set gates readiness/failure. Optional profiling/search/similarity failure can degrade features without failing an otherwise prepared import. Missing Required rows remain blocking.  
**Regression guard:** both terminal readiness and FAILED filtering derive Required membership from the media-type applicability authority, not from arbitrary persisted row presence.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static readiness trace complete; required/optional execution matrix remains R8.  
**Dependency findings checked:** X20, X21, X66  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 Stage-2 matrix.

---

## X26 — Forced shutdown lacks semantic Shutdown intent and can persist resumable work as CANCELLED

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Scheduler shutdown uses cancellation mechanisms, but resumable running work can observe generic cancellation and be persisted as CANCELLED.

### Root cause / failure path

Same mechanism problem as X19, specifically across process shutdown.

### Penyelesaian

Introduce explicit Shutdown control intent. Jobs interrupted by process shutdown remain durable/reclaimable according to retry/checkpoint policy and are reconciled at next startup.

### Penanggulangan

Shutdown during every job lane and handler phase. Restart must resume eligible work without user retry.

### Verification

CancelledDuringShutdown for resumable work must remain zero unless user Cancel was the actual intent.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `7f88baa130e108868ac9ed257f0f1e106206e6c7` (foundation `36d1104d347e0312906d3832ecdacd9cd0d96fe8`)  
**Primary remediation phase:** R4  
**Changed paths:** `JobScheduler.cs`; `JobCancellationOperations.cs`; `JobLeaseStore.cs`; `JobWrites.cs`.  
**Traceability checked:** scheduler shutdown, linked cancellation, handler Cancelled mapping, attempt accounting and restart reclaimability.  
**Root-cause correction:** Shutdown is a distinct control intent. Safe-boundary interruption under Shutdown atomically maps RUNNING→PENDING, clears transient completion/error fields and refunds the interrupted attempt instead of persisting CANCELLED. Catch ordering guarantees scheduler-token cancellation reaches this path.  
**Regression guard:** explicit user Cancel remains terminal; Pause remains PAUSED; process shutdown cannot permanently cancel resumable work.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static shutdown/restart trace complete; forced-process execution remains R8.  
**Dependency findings checked:** X19, X22, X51  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 process-kill/restart evidence.

---

# 10. X27–X29 — Reserved historical IDs

**Status:** RESERVED

X27, X28, and X29 are permanently reserved because historical continuity referenced them but their exact evidence/title/corrective record cannot be recovered reliably.

Rules:

- do not invent them;
- do not reuse them;
- do not shift X30 onward;
- do not count them as concrete findings.

---

# 11. Stage 6 — Profiling / Faces Findings

## X30 — CancelRequest cannot interrupt active worker inference promptly

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ProfilingRequestDispatcher processes AnalyzeFaces by awaiting the handler inside the same read loop that must receive CancelRequest. Although an in-flight CTS dictionary exists, the dispatcher cannot read the cancel envelope until the active synchronous/awaited request returns.

### Root cause / failure path

Transport receive loop and long-running request execution are serialized.

### Penyelesaian

Decouple request dispatch from the receive loop.

- read loop remains responsive;
- each cancellable request runs in a tracked bounded task;
- CancelRequest can cancel the target CTS immediately;
- writes to the transport are serialized safely;
- Shutdown cancels/drains all in-flight tasks;
- enforce bounded request concurrency.

### Penanggulangan

Cancellation latency test during real/slow YuNet/SFace inference. Cancel must be observed before natural inference completion.

### Verification

Worker receives CancelRequest while AnalyzeFaces is still active and returns deterministic CANCELLED.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `2c1ebd71abe0e7b65055d6d54709dae34b6c72e3` (foundation `36c0ef512a5a347033a28025bf6641f15a2fbb24`)  
**Changed paths:** `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs`.  
**Traceability checked:** app-side `ProfilingWorkerConnection` cancellation envelope → Worker receive loop → tracked per-request CTS/task → bounded long-running request slot → terminal response → shutdown drain.  
**Root-cause correction:** long-running AnalyzeFaces/ExtractStills work no longer monopolizes the transport receive loop. AnalyzeFaces physical inference is separately observed so `CancelRequest` can publish deterministic `CANCELLED` immediately even while one native OpenCV call is still unwinding; the tracked slot remains occupied until that native task terminates, preventing cancellation from creating hidden concurrency oversubscription.  
**Regression guard:** duplicate request IDs fail closed; long-running work is bounded; shutdown cancels and drains tracked tasks; response writes remain serialized by the transport.  
**Verification result:** SOURCE-TRACE VERIFIED. No app/test/native fault-injection execution was performed under the current execution restriction.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 must measure cancellation latency during real YuNet/SFace native inference and forced shutdown.

---

## X31 — ExpectedSha256 is carried to the worker but not proven against analyzed bytes

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

FaceAnalysisRequest contains ExpectedSha256, but the worker analysis path must prove the actual file bytes match it before inference. Without the gate, a replaced/missing/corrupt input can be interpreted as a valid analysis result, including Available with zero faces depending on downstream behavior.

### Root cause / failure path

Content identity authority is created by the app but not consumed by the worker at the trust boundary.

### Penyelesaian

Before decoding/inference:

- open the exact source under safe share semantics;
- compute/verify expected length/hash as appropriate;
- reject mismatch as content mismatch;
- distinguish missing/unreadable/corrupt from a genuine zero-face image;
- keep the verified object bound to the decode path where feasible.

### Penanggulangan

Missing, changed, same-name replacement, hash mismatch, unreadable, corrupt image, and valid zero-face cases.

### Verification

No hash mismatch may produce Available success.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `36c0ef512a5a347033a28025bf6641f15a2fbb24`  
**Changed paths:** `src/Neuterradise.Profiling.Worker/FaceAnalysis/FaceAnalyzer.cs`; `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs`; host error classification completed in `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/FaceAnalysisJobHandler.cs` at `fd4a5dd7a1e647cb6a646906fffc431d4228691c`.  
**Traceability checked:** authoritative Asset SHA → FaceAnalysisRequest.ExpectedSha256 → worker file-open/read lease → incremental SHA-256 verification → decode/inference lifetime → typed protocol error → job failure classification.  
**Root-cause correction:** the worker opens the exact input with write/delete sharing denied, hashes the bytes before model creation/inference, keeps that lease alive across decode/inference, and rejects changed/missing/unreadable input instead of projecting it as an Available zero-face result.  
**Regression guard:** malformed expected hashes, missing paths, unreadable input and SHA mismatch are distinct from genuine zero-face analysis; mismatch is classified as ContentMismatch by the host.  
**Verification result:** SOURCE-TRACE VERIFIED. The missing/changed/corrupt/valid-zero-face executable matrix remains R8.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 byte-replacement and corrupt-input execution evidence.

---

## X32 — IdentityBankProvider cache can remain stale after face confirmation changes samples

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

FaceDecisionOperations inserts/removes identity_samples and queues catalog invalidations, but IdentityBankProvider maintains its own loaded space cache. The decision path is not guaranteed to invalidate the affected embedding space cache.

### Root cause / failure path

Database invalidation and in-process identity-bank cache invalidation are separate mechanisms.

### Penyelesaian

After successful transaction commit, invalidate the exact affected EmbeddingSpaceKey(s), or version IdentityBankProvider against durable sample generation so stale cache cannot be returned.

Do not invalidate before commit.

### Penanggulangan

Confirm, change confirmation, reject/remove sample, then immediately request suggestions without restarting.

### Verification

Identity bank reflects the committed sample set on the next read.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `f2a6d5bf858fef03cd594d193f02f1325c7d9d9a` (shared-provider/post-commit foundation `fd4a5dd7a1e647cb6a646906fffc431d4228691c`)  
**Changed paths:** `src/Neuterradise.Runtime/Faces/IdentityBankProvider.cs`; `src/Neuterradise.Runtime/Faces/FaceDecisionOperations.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/FaceWrites.cs`.  
**Traceability checked:** committed identity_samples mutation → exact EmbeddingSpaceKey → all live IdentityBankProvider instances bound to the same CatalogDb → per-space generation invalidation → next GetSpaceAsync reload.  
**Root-cause correction:** cache invalidation now occurs only after successful transaction commit. Providers registered against the live CatalogDb are invalidated as one authority, and direct FaceWrites sample insertion uses the same post-commit invalidation path; pre-existing generation fencing prevents an in-flight stale load from being re-cached after invalidation.  
**Regression guard:** confirm/reassign/remove-and-recreate and direct sample insertion cannot leave the committed embedding space silently cached as the previous bank.  
**Verification result:** SOURCE-TRACE VERIFIED. Immediate post-decision suggestion refresh execution remains R8.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 cache-race and immediate-refresh evidence.

---

## X33 — SFace embedding is persisted with YuNet model provenance

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

FaceAnalysisJobHandler persists embedding-space information but the face detection row model_id/model_version assignment uses the YuNet request fields at the persistence site, while the embedding is produced by SFace.

### Root cause / failure path

Detection provenance and embedding provenance are collapsed into one pair of model fields.

### Penyelesaian

Separate provenance explicitly:

- detection model = YuNet ID/version;
- embedding model = SFace ID/version;
- embedding space key must match SFace provenance;
- identity_samples inherit the embedding model provenance, not detector provenance.

If the schema cannot represent both, migrate it.

### Penanggulangan

Schema and persistence assertions: every stored embedding’s model/version must match its embedding space and the Worker result that generated it.

### Verification

Persist one face and inspect detector and embedding provenance independently.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `fd4a5dd7a1e647cb6a646906fffc431d4228691c`  
**Changed paths:** `src/Neuterradise.Runtime/Faces/FaceDecisionOperations.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/FaceWrites.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0016_r5_face_embedding_provenance.sql`.  
**Traceability checked:** FaceAnalysisJobHandler detector persistence → face_detections YuNet model fields → canonical SFace EmbeddingSpaceKey → identity_samples model provenance → IdentityBankProvider load/matching.  
**Root-cause correction:** face_detections.model_id/model_version remain detector provenance, while every embedding-derived identity sample derives model_id/model_version from its canonical SFace embedding-space key. Migration 0016 repairs existing sample rows and adds database guards against future detector/embedding provenance collapse.  
**Regression guard:** FaceWrites validates sample model provenance against EmbeddingSpaceKey; face confirmation never copies YuNet detector fields into an SFace identity sample.  
**Verification result:** SOURCE-TRACE VERIFIED. Migration execution plus persisted detector-vs-embedding inspection remains R8.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 schema migration and persistence inspection.

---

## X34 — Worker crash breaker can restart indefinitely across successful handshakes

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ProfilingWorkerProcessHost resets ConsecutiveFailures to zero when handshake reaches Ready. A worker that repeatedly starts, handshakes successfully, then crashes can therefore avoid the MaxConsecutiveFailures breaker forever.

### Root cause / failure path

The breaker measures startup/handshake failures, not crash-loop stability.

### Penyelesaian

Use a crash-window/stability policy.

Only reset the breaker after a defined healthy interval or meaningful successful workload. Track crash timestamps/restart budget independently from handshake success.

### Penanggulangan

Simulate Ready→crash repeatedly faster than the stability window. Host must reach NeedsAttention after the bounded budget.

### Verification

No infinite worker restart loop.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `fd4a5dd7a1e647cb6a646906fffc431d4228691c`  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Jobs/ProfilingWorkerProcessHost.cs`.  
**Traceability checked:** StartAsync handshake → Ready timestamp → transport crash/NotifyCrash → restart counter → NeedsAttention threshold → controlled Shutdown reset.  
**Root-cause correction:** a successful handshake no longer resets the failure budget. The budget resets only after the previous Ready period survived the defined healthy stability window; repeated Ready→crash cycles inside that window therefore accumulate to the bounded breaker. Controlled shutdown resets the session-local failure counter explicitly.  
**Regression guard:** MaxConsecutiveFailures remains authoritative and fast Ready→crash loops cannot erase prior failures merely by handshaking successfully.  
**Verification result:** SOURCE-TRACE VERIFIED. Timed crash-loop execution remains R8.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 Ready→crash stability-window matrix.

---

## X35 — Identity bank can exceed the 4 MiB protocol frame limit

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ProfilingProtocol enforces MaximumFramePayloadSize = 4 MiB. BuildIdentityIndexRequest sends the complete sample set in one frame without a batching/chunking/cardinality contract.

### Root cause / failure path

Identity index transport scales with total sample count while protocol has a hard single-frame bound.

### Penyelesaian

Define bounded index transfer:

- chunked BuildIdentityIndex protocol, or
- paged append/finalize protocol;
- explicit maximum samples per frame derived from encoded size;
- reject impossible payload before serialization;
- preserve deterministic index signature/version.

### Penanggulangan

Boundary tests below, equal to, and above 4 MiB; large identity banks must build successfully through bounded frames.

### Verification

No supported library size can fail merely because one JSON frame exceeded the protocol maximum.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `fd4a5dd7a1e647cb6a646906fffc431d4228691c` (protocol/worker foundation `36c0ef512a5a347033a28025bf6641f15a2fbb24`)  
**Changed paths:** `src/Neuterradise.Profiling.Protocol/ProfilingProtocol.cs`; `src/Neuterradise.Profiling.Protocol/ProfilingContracts.cs`; `src/Neuterradise.Profiling.Worker/IdentityMatching/IdentityIndexCache.cs`; `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs`; `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/FaceAnalysisJobHandler.cs`.  
**Traceability checked:** IdentityBankProvider sample bank → deterministic bank signature → encoded-size-aware chunk producer → protocol v3 4 MiB frame gate → ordered worker transfer accumulator → final IdentityIndex publication → matching.  
**Root-cause correction:** BuildIdentityIndex is now a bounded ordered chunk protocol. Host chunking budgets the actual serialized sample size with envelope reserve and rechecks the complete envelope payload before every send; the worker requires a stable signature/model/space and contiguous chunk order and publishes the index only on the final chunk.  
**Regression guard:** no individual wire frame may exceed MaximumFramePayloadSize; oversized individual samples fail before transport; partial/out-of-order transfer does not become a matchable index.  
**Verification result:** SOURCE-TRACE VERIFIED. Boundary/equal/above-4-MiB large-bank execution remains R8.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 large-bank transport and frame-boundary evidence.

---

## X36 — ReleaseIndex is only guaranteed on the matching happy path

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

FaceAnalysisJobHandler sends ReleaseIndex after candidate matching, but cancellation/exception before that statement can leave the worker index cached until worker reset.

### Root cause / failure path

Remote resource lifetime is controlled by a normal-flow statement rather than finally/lease semantics.

### Penyelesaian

Wrap remote index lifetime in try/finally or an explicit remote lease abstraction. Release must be attempted with a cleanup token independent of the cancelled request, while preserving worker-disconnect tolerance.

### Penanggulangan

Cancel/fail after BuildIdentityIndex and at each MatchIdentityCandidates iteration. LoadedIndexCount must return to baseline.

### Verification

No request-local index leak after terminal completion.

### R5 implementation closure record — 2026-09-19

**Implementation SHA:** `fd4a5dd7a1e647cb6a646906fffc431d4228691c` (worker release acknowledgement foundation `36c0ef512a5a347033a28025bf6641f15a2fbb24`)  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Jobs/Handlers/FaceAnalysisJobHandler.cs`; `src/Neuterradise.Profiling.Worker/Dispatching/ProfilingRequestDispatcher.cs`; `src/Neuterradise.Profiling.Worker/IdentityMatching/IdentityIndexCache.cs`.  
**Traceability checked:** first BuildIdentityIndex attempt → build/match success/failure/cancellation → finally cleanup with independent bounded token → ReleaseIndex acknowledgement → loaded and pending worker index invalidation.  
**Root-cause correction:** remote index lifetime is now a finally-governed lease. Cleanup does not reuse the cancelled analysis token, worker ReleaseIndex is acknowledged as a request/response operation, and invalidation removes both a completed index and any partial chunk transfer.  
**Regression guard:** cancellation or failure after transfer start cannot exit the host matching scope without attempting independent cleanup; worker shutdown also invalidates all index state.  
**Verification result:** SOURCE-TRACE VERIFIED. LoadedIndexCount fault-injection checks remain R8.  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 cancel/fail-at-each-stage index-leak evidence.

---

# 12. Stage 7 — Profiles / Relations / Trash / Purge

## X37 — Profile Purge FK closure omits import_assignment_clusters references

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Profile purge dependency closure does not consistently include import_assignment_clusters.candidate_profile_id and decided_profile_id in preflight, affected-data snapshot, and delete ordering.

Physical recovery material can be deleted before the DB transaction discovers the remaining FK dependency.

### Root cause / failure path

Purge dependency enumeration and actual database FK graph are different authorities.

### Penyelesaian

Build one purge dependency closure from the schema/domain relationship set.

For assignment clusters:

- define lifecycle for candidate_profile_id;
- define lifecycle for decided_profile_id;
- do not blindly SET NULL where ACCEPTED decisions require durable semantics;
- include all affected rows in preflight/snapshot;
- perform final DB dependency preflight before first irreversible physical deletion.

### Penanggulangan

FK-on matrix:

- candidate only;
- decided only;
- both;
- accepted/completed/active assignment states;
- relation introduced after initial prepare;
- crash before DB commit;
- repeated recovery.

Terminal purge invariant: zero dangling references.

### Verification

Purge cannot delete recovery material and then fail on a previously omitted FK.


### R3 implementation closure record — 2026-09-19

**Implementation SHA:** `0e0aa2bc2c4218146f3a548bc1175904efe68220` (source implementation chain: `c82ab6997dbdd9155bff41acdfcad3b5a465eae4` → `0b8d6e1ef14152107db1e21c7c4aecd12d548454` → `0e0aa2bc2c4218146f3a548bc1175904efe68220`)  
**Primary remediation phase:** R3  
**Changed paths:** `src/Neuterradise.Runtime/Trash/PurgeExecutor.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0014_r3_trash_profile_lifecycle.sql`.  
**Traceability checked:** original Section 4B targets plus the live R2 reservation/publication authorities and recovery callers on the closing SHA.  
**Root-cause correction:** Profile Purge now treats every `import_assignment_clusters.candidate_profile_id` or `decided_profile_id` reference as a blocking dependency before irreversible deletion, includes both reference classes in the affected-data snapshot, and does not null an ACCEPTED decision (or candidate evidence) behind the assignment domain's back. After a purge authorization exists, DB triggers prevent a new candidate/decision reference from being attached to that Profile; Confirm revalidates the zero-reference condition before the first physical delete.  
**Regression guard:** The purge plan remains fail-closed: assignment references must be zero, the persisted PURGE_PROFILE reservation freezes new references, and the final DB deletion revalidates the same dependency/snapshot authority. Recovery bytes therefore cannot be destroyed and only afterward discover one of the previously omitted assignment-cluster FKs.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/authority/recovery trace completed; the fault-injection/restart matrix remains R8 executable evidence under AGENTS.md.  
**Dependency findings checked:** X08, X52, X54  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable crash/restart and FK-on matrices remain R8 evidence.


### R3 corrective revalidation record — 2026-09-19

**Corrective source SHA:** `d6063d8392bfbe522747f92a43767a71566b8cf6`  
**Review defect corrected:** assignment-cluster FK blocking was integrity-safe but its domain policy was implicit, making terminal assignment history look like an accidental permanent Purge dead-end.  
**Lifecycle policy:** `candidate_profile_id` and `decided_profile_id` remain fail-closed dependencies. PENDING candidate evidence must be resolved/recomputed before Purge. ACCEPTED `decided_profile_id` is durable historical decision authority and is never nulled implicitly by Purge. Terminal ACCEPTED/KEPT_UNKNOWN candidate evidence is likewise retained unless a separate assignment-domain operation explicitly changes it. Purge now reports these cases distinctly rather than folding them into an opaque aggregate blocker.  
**Irreversible-delete guard:** Profile Purge still requires zero assignment references before physical deletion, and migration `0015_r3_restore_purge_exclusion.sql` additionally prevents Purge from starting/advancing while Profile Restore owns the same recovery material.  
**Verification result:** static dependency/policy/TOCTOU trace completed. FK-on runtime matrix remains R8 evidence.  
**Source status:** SOURCE-CLOSED after corrective revalidation  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** only R8 executable evidence remains.

---

# 13. Stage 8 — Application / UI Integration Findings

## X38 — Profile hydration does not await UI commit

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Profile loading performs background awaits and then uses a UiDispatch.Run-style callback whose completion is not part of the awaited hydration operation. Callers can observe hydration complete while UI-bound state is still partial/default.

### Root cause / failure path

Dispatch scheduling is mistaken for dispatch completion.

### Penyelesaian

Use an awaitable InvokeAsync-style UI commit. Build immutable/background snapshots first, then commit one coherent UI state on the dispatcher.

### Penanggulangan

Test media, faces, related profiles, filters, page/sort state, overlapping loads, and stale generation.

### Verification

Load task may complete only after the intended UI generation has committed.

---

## X39 — Settings initialization mutates UI-bound state from worker continuation

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

After ConfigureAwait(false)/background reads, Settings initialization can mutate properties or ObservableCollection directly off the UI dispatcher.

### Root cause / failure path

Persistence read threading and UI state mutation are not separated.

### Penyelesaian

Read into immutable settings snapshot off-dispatcher, then perform all UI-bound mutation in one dispatcher commit.

### Penanggulangan

Force thread-pool continuations and enable collection/thread checks during tests.

### Verification

No cross-thread settings mutation.

---

## X40 — Language live state can change before durable persistence succeeds

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Language can be applied to the live presentation before the persistence write is proven successful. A failed write leaves current UI and restart state disagreeing.

### Root cause / failure path

Presentation commit precedes durable preference authority.

### Penyelesaian

Persist first, prove success, then apply live language; or use an explicit reversible pending state that rolls back on failure.

### Penanggulangan

Slow/failing write, rapid toggle, process close during write.

### Verification

After any failed persistence attempt, live and durable language authority remain consistent.

---

## X41 — Latest-value preference writes can mutate ThemeRuntime/resources from a worker

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Preference coalescing/latest-value logic can complete on a worker thread and invoke theme/resource mutation there.

### Root cause / failure path

Write serialization owns data ordering but not UI-thread affinity.

### Penyelesaian

Separate three phases:

read/compute → durable write → explicit UI dispatcher apply.

The coalescer may remain background, but resource dictionaries and UI runtime mutations are dispatcher-only.

### Penanggulangan

Rapid theme/layout changes with forced slow writes and background completion.

### Verification

Theme resources never mutate off dispatcher.

---

## X42 — Route/lifetime stale callback fencing is incomplete

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Pending media activation or queued async UI callback can outlive route suspension/disposal. StaleResultGuard disposal alone does not necessarily retire the active generation if already queued work is not rechecked inside the UI callback.

### Root cause / failure path

Cancellation token, route generation, and dispatch queue lifetime are separate.

### Penyelesaian

On route suspension/disposal:

- cancel pending interaction CTS;
- increment/retire route generation;
- mark disposed state;
- require generation/current-route check before scheduling and again inside the dispatcher callback.

### Penanggulangan

Navigate away before click timeout, queued callback during navigation, dispose, rapid old/new profile loads, scroll cancellation.

### Verification

No stale route can open detail, overwrite selection, or mutate a new surface.

---

# 14. Stage 9 — Updater / Package / Release / Security Findings

## X43 — CI NuGet package-root authority conflicts with package verification

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Workflow/build can set a custom NUGET_PACKAGES root while packaging/verification paths can assume the default/global package location.

### Root cause / failure path

Dependency acquisition and package verification do not share one NuGet root authority.

### Penyelesaian

All scripts consume the effective environment NUGET_PACKAGES authority. Do not rediscover a different default later.

### Penanggulangan

Clean custom-root, default-root, warmed-cache, and offline builds.

### Verification

Package/build succeeds from a non-default NuGet root without hidden fallback.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `scripts/package-win-x64.ps1`  
**Traceability checked:** Release workflow `NUGET_PACKAGES` environment → canonical restore → OpenCvSharp package-root verification → published Worker native runtime comparison.  
**Root-cause correction:** Package verification now resolves the effective `NUGET_PACKAGES` root supplied by CI and falls back to `%USERPROFILE%\.nuget\packages` only when no override exists.  
**Regression guard:** OpenCvSharp native verification fails closed unless exactly one restored pinned native DLL is found under the effective NuGet root and its bytes equal the published Worker DLL.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; canonical Release build remains R8/X56.  
**Dependency findings checked:** X44, X50, X56  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Exact canonical build execution remains R8 evidence.

## X44 — Release/package validation does not share one complete required-member contract

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Required executable/runtime members such as the app, updater, worker, model/runtime artifacts can be validated by different scripts/contracts. An incomplete deployment can pass one validator.

### Root cause / failure path

Package construction and package validation maintain separate required-member lists.

### Penyelesaian

Define one RequiredReleaseMembers authority consumed by:

- package script;
- release-manifest generation;
- self-validation;
- updater package validator;
- runtime startup prerequisites where appropriate.

### Penanggulangan

Negative matrix removing one required member at a time.

### Verification

No incomplete package reaches artifact/release acceptance.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHAs:** `f923926a62ac94654b754069d5f9457c38c33d36`, `f35a79d39b9b541fc60662222cccc2c1b73b0804`, `ebdd445359c5d186eee83acff4a52be17a6253a6`  
**Primary remediation phase:** R1  
**Changed paths:** `release-contract.json`; `scripts/package-win-x64.ps1`; `scripts/build.ps1`; `src/Neuterradise.Release.Contracts/ReleaseContract.cs`; `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageValidator.cs`; `src/Neuterradise.Updater/ReplacementEngine.cs`; six canonical `packages.lock.json` files.  
**Traceability checked:** canonical release contract → package producer → release/update manifest membership → runtime package validator → updater staged-copy validator → canonical build output and dependency authority.  
**Root-cause correction:** The shared release contract now requires the complete mandatory first-party/control launch closure for both app and Worker, including app executable/assembly/deps/runtimeconfig, Runtime, Profiling.Protocol, Release.Contracts, Worker executable/assembly/deps/runtimeconfig, Worker protocol/contracts/OpenCvSharp managed runtime, models, media tools, deployment metadata, updater, and notices. Exact payload membership remains closed by release/update manifests, while the dependency graph is separately locked by X50.  
**Regression guard:** Removing any contract member causes package/build/runtime/update validation to fail closed; app/Worker project dependency changes also change checked-in NuGet lockfiles and the canonical dependency provenance digest.  
**Verification result:** SOURCE-TRACE VERIFIED. Contract consumers and generated dependency authority were re-read from `main`; canonical build/package/runtime execution remains R8.  
**Dependency findings checked:** X02, X03, X43, X49, X50, X57, X59, X60, X65, X71  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Full executable package launchability is R8 evidence, not an R1 source blocker.
## X45 — Update trust lacks independent publisher authenticity

**Status:** CONFIRMED-HARDENING / OPEN-IMPLEMENTATION

### Temuan

Hashing proves integrity relative to metadata but does not independently prove who published the metadata/package.

### Root cause / failure path

Transport/repository location and hashes are treated as sufficient publisher trust.

### Penyelesaian

Use pinned publisher authenticity:

- signed update metadata or equivalent verifiable release signature;
- pinned public key/trust root;
- signed version, package digest, membership, compatibility, and release identity;
- rollback/revocation policy.

### Penanggulangan

Reject missing, wrong-key, modified, expired/revoked where supported, and rollback signatures.

### Verification

Tampered but self-consistent manifest+package must still fail publisher authentication.

---

## X46 — Build workflow combines write permission with movable action tags

**Status:** CONFIRMED-HARDENING / OPEN-IMPLEMENTATION

### Temuan

Release workflow uses contents: write for a job that also builds and consumes actions by mutable major tags such as actions/checkout@v4 and actions/setup-dotnet@v4.

### Root cause / failure path

Build trust and publication authority are combined, increasing supply-chain blast radius.

### Penyelesaian

- Build job: read-only permissions.
- Publish job: minimal write permission, consuming only verified artifact/provenance.
- Pin third-party/action revisions to immutable commit SHAs according to repository update policy.

### Penanggulangan

Workflow policy check rejects write-enabled build jobs and unapproved movable action references.

### Verification

Publish authority cannot modify build inputs or silently replace the verified artifact.

---

## X47 — Successful update does not have a fully convergent terminal cleanup contract

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Operation-scoped staging, backup, tools, handoff, and recovery material can survive successful completion or be deleted at the wrong recovery phase.

### Root cause / failure path

Recovery preservation and terminal cleanup are not modeled as one state machine.

### Penyelesaian

Define cleanup ownership by terminal state:

- preserve artifacts required for recovery while state is ambiguous/nonterminal;
- after verified replacement success, remove obsolete backup/staging/tools deterministically;
- cleanup itself is retryable/idempotent;
- never remove the only rollback evidence before success verification.

### Penanggulangan

Crash during every cleanup step and rerun recovery.

### Verification

Successful update converges to one clean terminal topology; interrupted update preserves exactly what recovery needs.

---

## X48 — Corrupt persisted updater state/handoff can escape typed recovery classification

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Missing, valid, and corrupt persisted update artifacts require different handling. A parse/format failure must not become an untyped exception that bypasses deterministic startup recovery.

### Root cause / failure path

Persistence parsing and recovery safety classification are coupled by exceptions rather than explicit result types.

### Penyelesaian

Return typed states:

- Missing;
- Valid;
- Corrupt.

Corrupt authority yields deterministic RecoveryRequired/blocked startup as appropriate. Never delete catalog, backup, or staging merely to make parsing succeed.

### Penanggulangan

Malformed JSON, truncated file, wrong operation ID, unsafe path, invalid recovery step, and manifest corruption tests.

### Verification

Every corrupt state has a deterministic user/recovery outcome.

---

## X49 — MinimumCompatibleVersion is declared but not enforced

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

UpdateManifest exposes MinimumCompatibleVersion, but UpdateTrustPolicy does not enforce installed-version compatibility. Current v0.0.1 packaging may write null, leaving the defect dormant until a non-null minimum is published.

### Root cause / failure path

Compatibility metadata is a dead contract.

### Penyelesaian

- parse version strictly;
- reject malformed minimum;
- if non-null, compare installed ProductIdentity.Version;
- reject candidate when installed version is below the minimum required upgrade baseline;
- define behavior for future schema/major compatibility.

### Penanggulangan

installed version below, equal, above; null; malformed; future incompatible candidate.

### Verification

Trust decision must fail before staging when compatibility is not satisfied.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** f937fc8a2d85638107b6753e38bde8e41d1a16e0  
**Primary remediation phase:** R1  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Updates/UpdateManifest.cs`; `src/Neuterradise.Runtime/SystemServices/Updates/UpdateTrustPolicy.cs`  
**Traceability checked:** update manifest parser → product/runtime identity → strict candidate version → installed version → `MinimumCompatibleVersion` → trust decision → handoff.  
**Root-cause correction:** `MinimumCompatibleVersion` is now parsed and enforced; the installed product must meet it, the minimum cannot exceed the candidate, and cross-major updates require an explicit minimum authority.  
**Regression guard:** Noncanonical/non-numeric version strings, incompatible installed versions, impossible minimums, and cross-major updates without an explicit minimum fail closed before staging/handoff.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; update acceptance execution remains R8.  
**Dependency findings checked:** X02, X44, X45, X60  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** Executable update compatibility cases remain R8 evidence.

## X50 — Canonical toolchain/dependency graph is not reproducibly locked

**Status:** CONFIRMED-HARDENING / SOURCE-CLOSED

### Temuan

global.json does not pin an exact .NET SDK with an explicit roll-forward policy, workflow uses 10.0.x, and the dependency graph lacks a complete locked-restore authority/provenance record.

### Root cause / failure path

The same source SHA can restore/build against different SDK or dependency resolution over time.

### Penyelesaian

- pin exact .NET SDK and roll-forward policy;
- use package lock files/locked restore appropriate to the solution;
- record SDK identity and lockfile digest in build provenance;
- make CI/local canonical build consume the same contract.

### Penanggulangan

Clean locked restore must fail when dependency graph drifts. SDK/dependency changes require intentional review and provenance change.

### Verification

Rebuild the same SHA from a clean environment and compare provenance/package membership.

---

### R1 implementation closure record — 2026-09-19

**Implementation SHAs:** `f923926a62ac94654b754069d5f9457c38c33d36`, `f35a79d39b9b541fc60662222cccc2c1b73b0804`, `ebdd445359c5d186eee83acff4a52be17a6253a6`  
**Primary remediation phase:** R1  
**Changed paths:** `global.json`; `Directory.Build.props`; `scripts/build.ps1`; `.github/workflows/release.yml`; `.github/workflows/security.yml`; six project-local `packages.lock.json` files.  
**Traceability checked:** exact SDK resolver → Actions setup-dotnet → one lockfile per solution project → canonical locked restore → release cache key → build provenance.  
**Root-cause correction:** .NET SDK is pinned to 10.0.401 with `rollForward=disable`; all six projects now have authentic NuGet lockfiles generated by SDK 10.0.401 on GitHub Actions; canonical restore runs `--locked-mode`; `RestorePackagesWithLockFile` and `RestoreLockedMode` are repository-wide authorities.  
**Regression guard:** Canonical restore fails on dependency drift, release cache identity includes lockfiles, build refuses a non-10.0.401 SDK, and build provenance records both the full dependency-authority digest and a dedicated aggregate `dependencyLockSha256`.  
**Verification result:** GitHub Actions run `35443876832` SUCCESS generated six lockfiles from the exact SDK and committed them as `f35a79d39b9b541fc60662222cccc2c1b73b0804`. Each project has one schema-v1 lockfile. Canonical Release build remains R8.  
**Dependency findings checked:** X43, X44, X46, X56  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** No R1 source blocker; reproducible build equivalence remains R8 verification.

# 15. Stage 10 — Cross-Domain Adversarial Findings

## X51 — Final-attempt crash does not leave the current JobRecovery path permanently RUNNABLE

**Status:** REJECTED-AS-INDEPENDENT-FINDING

### Temuan audit sebelumnya

The earlier ledger claimed that an interrupted final attempt could be reconciled back to PENDING/RUNNABLE while claim logic rejected it because `attempt >= max_attempts`.

### Falsification result

The current `JobRecovery.ReconcileAsync` does not implement that failure path.

For an interrupted leased job it first writes `FAILED_RETRYABLE`. It returns a requeue finding only when `job.Attempt < job.MaxAttempts`. On the final/exhausted attempt it calls `TryExhaustRetriesAsync` and returns `JOB_RETRIES_EXHAUSTED` / NeedsAttention instead of deliberately requeueing the job.

Relevant path:

- `src/Neuterradise.Runtime/SystemServices/Recovery/JobRecovery.cs`

### Canonical disposition

Do not implement X51.

Other scheduler/recovery defects remain real, including X19, X21, X26, X53, and X66.

### Penanggulangan

Retain a regression case for crash-on-final-attempt because it protects this already-correct branch from regression.

### Verification

A final-attempt interrupted job must not become a claimable/reported RUNNABLE job. X51 remains rejected unless that invariant is later disproved by executable evidence.

## X52 — Partial startup rollback can release VaultLock before mutation-capable runtime stops

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

When startup fails after some services/writers have activated, rollback ordering can dispose/release VaultLock before every mutation-capable component is guaranteed stopped.

### Root cause / failure path

Startup acquisition and rollback are not one strict lifetime stack.

### Penyelesaian

Treat startup as transactional resource acquisition.

- register each acquired component;
- rollback in reverse dependency order;
- stop mutation producers/writers first;
- close DB/runtime;
- release VaultLock last.

### Penanggulangan

Fault inject after every startup activation boundary. A second process must not acquire the Vault while any first-process writer remains capable of mutation.

### Verification

Two-process startup-failure lock test.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `2ba612666a718298438de04e290fbef2b9ae509d` (surface hardening `b556964fed320313caf32fe4b2570f4dd36f000e`)  
**Primary remediation phase:** R4  
**Changed paths:** `AppBootstrapper.cs`; `StartupResult.cs`; `src/Neuterradise.App/App.xaml.cs`.  
**Traceability checked:** Prewarm runtime creation, ShowShell activation, Finalizer/activity/runtime/resource ownership, callback failure, context disposal and retry UI.  
**Root-cause correction:** bootstrap invokes an explicit runtime rollback authority before disposing BootstrapContext. If rollback cannot prove quiescence, startup fails closed as `STARTUP_ROLLBACK_UNSAFE` and retains BootstrapContext/VaultLock until process exit.  
**Regression guard:** retained-authority failure is non-retryable; ordinary rollback follows reverse acquisition before lock release.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static reverse-order trace complete; injected startup failures remain R8.  
**Dependency findings checked:** X22, X23, X54  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 startup fault-injection evidence.

---

## X53 — NeedsAttention authority ambiguity can pass startup because the critical gate does not cover path-state/recovery ambiguity

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

`AppBootstrapper` aborts startup only when `RecoveryResult.HasFatal` is true. `StorageRecovery` can classify path reconciliation conflicts/ambiguity as `NeedsAttention`.

The following `CriticalIntegrityGate` checks owner/identity/appearance invariants, but it does not check `path_state = NEEDS_ATTENTION` or equivalent unresolved path-reconciliation authority.

Relevant paths:

- `src/Neuterradise.Runtime/SystemServices/Lifecycle/AppBootstrapper.cs`
- `src/Neuterradise.Runtime/SystemServices/Recovery/StorageRecovery.cs`
- `src/Neuterradise.Runtime/SystemServices/Lifecycle/CriticalIntegrityGate.cs`

### Root cause / failure path

Presentation severity (`NeedsAttention`) is also being used as startup-safety policy, while some NeedsAttention codes represent unresolved writable authority.

### Penyelesaian

Add explicit startup-safety classification independent of presentation severity:

- advisory;
- retryable maintenance;
- blocks affected capability;
- blocks writable startup;
- fatal.

Path reconciliation ambiguity, unresolved mutation authority, and equivalent states must block writable startup until reconciled or explicitly isolated.

### Penanggulangan

Maintain a recovery-code → safety-class table and make startup tests cover every code.

### Verification

A persisted unresolved path authority cannot reach the normal writable shell merely because its recovery finding is labeled NeedsAttention.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `97ebefaa36dc77772e706e92246beeb0e70dc4a4`  
**Primary remediation phase:** R4  
**Changed paths:** `RecoveryResult.cs`; `AppBootstrapper.cs`; `CriticalIntegrityGate.cs`.  
**Traceability checked:** RecoveryFinding outcome/code, StorageRecovery managed-path findings, startup recovery decision and persisted Profile/Asset `path_state`.  
**Root-cause correction:** recovery severity and writable-startup safety are separate authorities. Bootstrap blocks Fatal or BlocksWritableStartup findings, and CriticalIntegrityGate independently rejects any Profile/Asset still PENDING or NEEDS_ATTENTION after recovery.  
**Regression guard:** unresolved managed-path authority cannot enter the normal writable shell even when surfaced as NeedsAttention.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static recovery/path trace complete; ambiguity startup execution remains R8.  
**Dependency findings checked:** X04, X05, X17, X52  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 recovery/startup evidence.
## X54 — Global mutation command admission remains open while shutdown is already awaiting

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ShutdownCoordinator supports stopAcceptingCommands, but production wiring can leave it null. Controlled shutdown performs asynchronous work, so new Profile/Trash/Import/Settings/update mutations can enter after shutdown begins.

### Root cause / failure path

No process-wide mutation admission gate is closed synchronously before the first await.

### Penyelesaian

At the first transition to ShuttingDown:

1. synchronously close global mutation admission;
2. reject new mutating commands;
3. drain/cancel active command leases;
4. quiesce finalizer/scheduler/writers;
5. flush;
6. release VaultLock last.

### Penanggulangan

Concurrency tests issue every mutation family while Close begins. After ShuttingDown, accepted new mutation count must be zero.

### Verification

Clean marker may be written only after command leases are zero and mutation authorities are closed.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `469eee8b14609032cee280f9bf060d858dc98550` (authority chain `b11d19ac4212e6aeaf52c24aeaf0b0750110b90f` → `d8220b92e6afc158d3cee4f7e7018be7c717b972` → `ea9ea52d61dbbab7c55fc4c04752f2e930fec102` → `10d1a740a8146db6518405af886fb951c41d720b` → `64dd85bebc4dff8b8402695e6d33a3de1bf0d4a1` → `be22c9c51b2b5ddaa96a22f590ae2f5fa304ae96` → `469eee8b14609032cee280f9bf060d858dc98550`)  
**Primary remediation phase:** R4  
**Changed paths:** `CatalogMutationAdmissionGate.cs`; `CatalogDb.cs`; `src/Neuterradise.App/App.xaml.cs`; `ShutdownCoordinator.cs`; mutating Profile/ProfileAppearance/UnknownResolution/Media/Trash/Purge/ImportIntake/ImportControl/Verification/Publication/Face/Related/Settings/Taxonomy/Update command boundaries.  
**Traceability checked:** every mutation family named by X54 plus Verify/taxonomy/related paths found during caller sweep; top-level Close, active lease drain, Finalizer/scheduler/services, clean marker and VaultLock release.  
**Root-cause correction:** one process-wide re-entrant admission gate is synchronously closed at `ControlledShutdownAsync` entry before the first await. Existing command leases drain before quiescence; ShutdownCoordinator rechecks the same authority before clean-marker/lock release.  
**Regression guard:** post-Close mutation acquisition throws `MutationAdmissionClosedException`; nested work remains covered by the admitted outer command; clean shutdown requires zero active leases.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static ingress sweep/order trace complete; mutation-storm execution remains R8.  
**Dependency findings checked:** X20, X22, X23, X52, X55  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 concurrency execution evidence.

### R4 post-review corrective closure — 2026-09-19

**Corrective SHA:** `2828b2b1eee8925b021ae557705a5ed12aa218ea`  
**Review defect:** `ImportViewModel.RetryUnitAsync`, `ClearHistoryItemAsync`, and `ClearHistoryAsync` were direct durable-write paths outside `CatalogMutationAdmissionGate`; Retry also performed job writes outside the per-unit authority.  
**Correction:** all three commands now route through `ImportUnitControlAuthority`. Retry/Clear-one acquire process-wide admission plus per-unit serialization; Clear-all acquires process-wide admission and executes a terminal-only bulk write under the Catalog write coordinator. The ViewModel no longer owns those durable write sequences.  
**Static revalidation:** after `MutationAdmission.Close()`, these previously uncovered Import commands can no longer begin a new mutation.  
**Source status:** SOURCE-CLOSED after corrective review  
**Runtime status:** NOT-YET-VERIFIED — mutation-storm shutdown execution remains R8.

---

## X55 — Application shutdown deadline and updater parent-wait contract are not one end-to-end deadline

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ShutdownCoordinator has a 10-second default budget, while updater helper waits up to 60 seconds for the parent. Work before the bounded shutdown phase can consume untracked time, causing the helper to end at AbortedParentStillRunning even though update handoff has begun.

### Root cause / failure path

App and updater own separate clocks with no shared handoff deadline/state.

### Penyelesaian

Use one top-level shutdown/update handoff deadline beginning at ControlledShutdown entry.

Persist explicit HandoffPending/deferred/aborted state. Updater replacement begins only after confirmed parent exit. Timeout produces deterministic deferred/aborted recovery, not ambiguous partial replacement.

### Penanggulangan

Slow Finalizer, slow persistence, slow filesystem, slow worker shutdown, and updater handoff tests near all deadline boundaries.

### Verification

No update attempt can be stranded merely because pre-shutdown work consumed time outside the advertised budget.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `0c501772daccade57cd8f4b7720b17f15dc56852` (command-drain integration finalized `469eee8b14609032cee280f9bf060d858dc98550`)  
**Primary remediation phase:** R4  
**Changed paths:** `UpdateCoordinator.cs`; `UpdateManifest.cs`; `UpdateRecoveryPlan.cs`; `UpdateStartupRecovery.cs`; `src/Neuterradise.App/App.xaml.cs`; `src/Neuterradise.Updater/ReplacementEngine.cs`; existing `UpdateHandoffService.cs` transports the trusted handoff.  
**Traceability checked:** staging/validation, HandoffPending, helper launch, top-level app shutdown work, remaining budget, parent wait, pre-mutation timeout journal and next-start recovery.  
**Root-cause correction:** handoff carries one absolute `ShutdownDeadlineUtc`; app shutdown and updater parent wait consume the same deadline rather than owning independent clocks. Deadline expiry persists `DeferredShutdownDeadlineExpired` and aborts before InstallRoot mutation; startup recovery retires that safe pre-mutation attempt deterministically.  
**Regression guard:** updater rejects handoff without deadline; replacement cannot begin before parent exit; timeout cannot become ambiguous partial replacement.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static end-to-end deadline trace complete; slow boundary execution remains R8.  
**Dependency findings checked:** X22, X47, X48, X54  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 updater/shutdown timing matrix.

---

# 16. Stage 11 — Build / Package / Runtime Verification Findings

These findings are evidence defects. They are not satisfied by source inspection.

## X56 — Exact-tree canonical build evidence is absent

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan

The audited tree has no accepted evidence proving the exact source baseline can complete the canonical Windows Release build.

### Root cause / evidence gap

The build/release workflow is not itself evidence of an executed build for the audited SHA. A manual workflow definition, source-level compile plausibility, or historical local package cannot prove that the exact audited tree restores and builds successfully under the canonical toolchain.

### Penyelesaian

Run the canonical build on an exact recorded SHA with:

- SDK identity;
- lock/dependency identity;
- build command;
- artifact manifest;
- source SHA;
- timestamps/log outcome.

The canonical command remains the repository-owned build path; do not invent an alternate build.

### Penanggulangan

Every release candidate records exact-tree build provenance.

### Verification

Canonical Release win-x64 build PASS on the intended fixed SHA.

---

## X57 — Package integrity validation is not runtime execution

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan

ZIP membership/hash/self-validation can prove package structure but cannot prove that the extracted product starts and its runtime paths resolve correctly.

### Root cause / evidence gap

Static package validation terminates before process creation, native loader resolution, model/tool discovery, AppState/Vault bootstrap, and actual application startup. Package integrity and runtime executability are different authorities.

### Penyelesaian

Keep two separate gates:

- PACKAGE_INTEGRITY_OK;
- RUNTIME_VERIFIED.

Never derive the second from the first.

### Penanggulangan

Release automation/reporting must preserve the distinction.

### Verification

Extract package to a disposable path and launch from that extracted layout.

---

## X58 — No isolated deterministic runtime verification harness

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan

A repeatable verification environment is required to exercise startup and stateful lifecycle without touching a real user Vault or depending on ambient machine state.

### Root cause / evidence gap

Without an isolated harness, runtime checks depend on developer-machine state and cannot safely inject failures, restart boundaries, or destructive lifecycle scenarios. That makes the evidence non-repeatable and risks contaminating a real Vault.

### Penyelesaian

Create an isolated verifier using:

- temporary AppStateRoot;
- temporary VaultRoot;
- temporary/install extraction root;
- deterministic fixtures;
- bounded timeouts;
- captured diagnostics;
- cleanup.

### Penanggulangan

Verification must be safe to repeat and must fail closed on timeouts/residue.

### Verification

Cold start, warm start, shutdown, and restart in the isolated environment.

---

## X59 — Packaged Worker/OpenCV/YuNet/SFace E2E probe is absent

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan

Static package inspection does not prove native OpenCV load, worker protocol handshake, model path, model hash, YuNet detection, or SFace embedding.

### Root cause / evidence gap

Worker correctness crosses process, protocol, native ABI, packaged-path, and model-artifact boundaries. None of those boundaries is executed by source reading or ZIP hash validation.

### Penyelesaian

Add packaged-worker E2E:

- launch Worker from packaged layout;
- Hello/ack protocol;
- confirm model availability;
- analyze a known face fixture;
- verify detection;
- verify 128-value embedding and provenance;
- cancel one request;
- release index;
- clean shutdown.

### Penanggulangan

Run on release candidates and after any worker/model/OpenCV/package topology change.

### Verification

Real packaged inference PASS.

---

## X60 — Updater disposable-install replacement/recovery E2E is absent

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan

Updater replacement/recovery correctness spans multiple processes and filesystem states and cannot be proven by unit/source reasoning alone.

### Root cause / evidence gap

The updater's safety properties depend on parent-process exit timing, operation-scoped staging, sibling backup/install directories, crash timing, journal durability, and restart reconciliation. Source inspection cannot prove the combined filesystem state machine.

### Penyelesaian

Create disposable InstallRoot scenarios:

- normal replacement;
- parent still running;
- staging mutation;
- crash after staging prepared;
- crash after InstallRoot→backup;
- crash after staged→InstallRoot;
- restore success/failure;
- corrupt recovery journal;
- successful terminal cleanup;
- Vault disjointness.

### Penanggulangan

Run for updater/release changes.

### Verification

Every scenario converges to known installed, restored, blocked-recovery, or safely aborted state.

---

## X61 — Stateful lifecycle matrix is not an executable regression gate

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan

The audit identified lifecycle/race failures across import, jobs, Trash, Profile, startup, and shutdown. Without executable regression coverage, the same classes can return during remediation.

### Root cause / evidence gap

Historically, source fixes were accepted without one executable matrix that replays the cross-domain invariants and adversarial boundaries that produced the defects. That allows a local fix to regress a sibling lifecycle without an immediate gate.

### Penyelesaian

Turn the audit matrices into automated gates covering:

- import COPY/MOVE/reuse;
- Pause/Resume/Cancel/Shutdown;
- crash/restart;
- Asset Trash/Restore/Purge;
- Profile Trash/Restore/Purge;
- rename/relocation;
- worker/cache/index lifecycle;
- updater handoff/recovery;
- UI stale callback/thread affinity where testable.

### Penanggulangan

A fixed finding is not complete until its regression guard is committed with it or a documented executable harness owns that invariant.

### Verification

All finding-specific guards plus cross-domain matrix PASS on the fixed tree.

---

# 17. Stage 12 — Convergence / Audit-Control Findings

## X62 — Historical ledger provenance gap for X27–X29

**Status:** CLOSED-DOC / AUDIT-CONTROL

### Temuan

History references a prior range reaching X29, but exact reliable records for X27–X29 cannot be recovered.

### Root cause / audit-control cause

Historical finding identity and provenance were not committed atomically into one immutable canonical register. Surviving references prove that the IDs existed, but not enough trustworthy evidence survives to reconstruct their titles, source paths, or remediation contracts without invention.

### Penyelesaian

Reserve X27–X29 permanently. Do not fabricate contents and do not renumber later findings.

### Penanggulangan

Future finding register records ID, stage, title, evidence, root cause, solution, prevention, verification, status, and provenance atomically.

### Verification

The canonical ledger must contain no X27/X28/X29 finding body, must mark all three IDs RESERVED, and must preserve every later ID unchanged.

---

## X63 — Historical finding-ID collision in an old Stage 7 response

**Status:** CLOSED-DOC / AUDIT-CONTROL

### Temuan

An old Stage 7 response reused X06–X09 for Profile/Trash findings, colliding with established Stage 3 and Stage 4 IDs.

### Root cause / audit-control cause

Finding IDs were previously allocated in conversational output without enforcing the canonical ledger as the sole allocator. That allowed a later stage to reuse identifiers already owned by earlier findings.

### Penyelesaian

Canonical identity is frozen:

- X06–X08 = Stage 3.
- X09 = Stage 4 same-volume replay.
- Profile Purge FK closure incomplete = X37.

The old aliases are superseded.

### Penanggulangan

Finding IDs are immutable. New findings allocate max canonical ID + 1 only.

### Verification

A ledger validation pass must report every concrete finding ID exactly once, no duplicate X06–X09 aliases, X37 as the sole canonical Profile Purge FK-closure finding, and X74 as the next available ID after this certification.

---

# 18. Stage 12 — Final Coverage Reconciliation Findings

The final coverage certification compared the canonical X ledger against the complete pre-stage historical current-audit register D38–D57 and the live frozen source tree. Ten active findings had been lost during ledger consolidation. They are not newly discovered defects; they are previously confirmed defects that were missing from the X register. They are restored here as X64–X73.

## X64 — Update ZIP extraction safety is not explicit enough for static security proof

**Status:** CONFIRMED-HARDENING / SOURCE-CLOSED

### Temuan

UpdatePackageStager performs substantial containment defense through RootPathRules.ResolveContainedPath(), including a second resolution immediately before file mutation. The historical CodeQL Zip Slip finding nevertheless remained because the extraction boundary sends an archive-derived name through a custom sanitizer that the analyzer does not recognize as an explicit local containment proof.

This finding is a security-proof/quality defect, not a claim that a working Zip Slip exploit has been demonstrated.

Relevant path:

- src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs

### Root cause / failure path

Archive entry name → normalization → custom ResolveContainedPath helper → FileStream.

The runtime helper can be strong while the security-analysis dataflow still sees archive-controlled path material reaching a file sink without a recognizable explicit canonical-root check at the extraction boundary. That leaves the source difficult to prove for both static analysis and future maintainers.

### Penyelesaian

At the extraction boundary, before creating any directory or file:

1. canonicalize the operation staging root;
2. derive the candidate destination from the archive entry;
3. call Path.GetFullPath on the candidate;
4. perform an explicit boundary-safe root-containment check using the shared RootPathRules authority;
5. reject rooted, traversal, drive/colon, invalid, or escaping entries;
6. keep ResolveContainedPath and reparse-point validation as defense-in-depth;
7. perform the containment proof again immediately before file creation after parent-directory creation.

Do not weaken the current reparse-point protections merely to silence CodeQL.

### Penanggulangan

- Keep CodeQL/security workflow as a required security gate for extraction changes.
- Add malicious ZIP fixtures: ../, ..\, absolute path, sibling-prefix escape, alternate separators, duplicate canonical name, reparse redirection, and path normalization variants.
- Add valid nested-file and valid directory-entry fixtures.
- Any future archive extraction implementation must use the same shared containment authority.

### Verification

The security query must no longer report the extraction sink, and all malicious fixtures must fail closed while a canonical release ZIP stages successfully.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHA:** `f923926a62ac94654b754069d5f9457c38c33d36`  
**Primary remediation phase:** R1  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs`  
**Traceability checked:** ZIP entry name → file/directory classification → canonical segment normalization → duplicate canonical-name authority → full-path staging containment → shared reparse-aware authority → immediate post-directory/pre-file mutation re-resolution.  
**Root-cause correction:** Safe directory entries are now accepted instead of being rejected merely for a trailing slash. Directory entries must contain zero file data, use the same canonical containment/reparse authority as files, and are re-resolved after creation. Files retain the immediate pre-write containment proof.  
**Regression guard:** rooted/traversal/drive/NUL/empty-segment entries, canonical file-directory collisions, data-bearing directory entries, containment disagreement, reparse redirection, oversized entries, and compression bombs fail closed; valid directory entries and valid nested files are permitted by the source contract.  
**Verification result:** SOURCE-TRACE VERIFIED; Security Scan for the R1 source change is tracked separately. Exact malicious/valid ZIP fixture execution remains X65/R8.  
**Dependency findings checked:** X02, X44, X60, X65  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** X65 executable ZIP matrix remains R8 evidence, not an R1 source blocker.
## X65 — Canonical ZIP directory-entry incompatibility requires executable artifact evidence

**Status:** EXECUTION-EVIDENCE-GAP

### Temuan / evidence gap

The historical register claimed that the ZIP produced by `Compress-Archive` contains explicit directory entries that `UpdatePackageStager` rejects because normalized names ending in `/` are refused.

### Root cause / evidence gap

The stager's rejection rule is visible in source, and the package script uses `Compress-Archive`. Static source alone, however, does not prove the exact central-directory entry set emitted by the PowerShell/.NET version used by the canonical Windows packaging environment.

Therefore the current root cause is an **evidence boundary**, not a certified application defect: package producer behavior and staging consumer behavior have not yet been executed against the same exact artifact.

### Required evidence

During X57/X60 package-runtime verification:

1. build the exact canonical ZIP;
2. enumerate every central-directory entry;
3. feed that exact ZIP to `UpdatePackageStager`;
4. record whether directory entries are emitted and whether staging accepts them.

### Resolution state machine

X65 has exactly two legal evidence outcomes:

1. **Reproduced:** append the artifact evidence and reclassify the same X65 from `EXECUTION-EVIDENCE-GAP` to `CONFIRMED-SOURCE / OPEN-IMPLEMENTATION`. Only after that reclassification is a source patch authorized.
2. **Not reproduced:** append the artifact evidence and set X65 to `RESOLVED-NOT-REPRODUCED`. No source patch is authorized and the canonical ZIP remains a permanent regression input.

Do not allocate a new finding ID merely because X65 transitions from evidence gap to confirmed source defect.

### Penyelesaian if reproduced

After and only after the **Reproduced** transition, update the stager to recognize safe contained directory entries while preserving traversal, duplicate, reparse, and membership protections.

If the canonical ZIP contains no such entries or the stager accepts the artifact, no stager change is permitted for X65.

### Penanggulangan

The exact canonical release ZIP must remain an input to updater E2E verification so packaging and staging can never silently diverge.

### Verification

X65 is resolved only by artifact execution evidence, not by source inference.

## X66 — Cancellation terminal projection is delayed until recovery polling on cancellation paths

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

JobScheduler.CompleteAttemptAsync invokes OnJobCompleted for success and terminal failure, but the JobRetryOutcome.Cancelled branch increments cancellation metrics/finalizes and exits without invoking the completion observer. Idle cancellation also transitions durable job state directly. Stage2CompletionHandler reconciliation is therefore able to depend on the bounded OnReconciled recovery poll, whose interval is 750 ms.

Relevant paths:

- src/Neuterradise.Runtime/SystemServices/Jobs/JobScheduler.cs
- src/Neuterradise.Runtime/Import/Preparation/Stage2CompletionHandler.cs
- src/Neuterradise.Runtime/SystemServices/Lifecycle/ProductionRuntimeRegistry.cs

A job can already be CANCELLED while dependent capability/readiness projection is still stale.

### Root cause / failure path

Durable terminal cancellation and semantic terminal projection are separate events. Cancellation paths do not publish the same immediate closure signal as other terminal outcomes.

### Penyelesaian

After a cancellation state is durably committed:

- immediately invoke an idempotent terminal-projection/closure path;
- close impossible dependents;
- project affected capability states;
- re-evaluate affected ImportUnit readiness;
- raise scheduler/status wake signals.

Keep the 750 ms reconciliation loop only as a recovery safety net for missed signals, restart reconciliation, or older persisted states.

The immediate path must be idempotent so observer failure/retry cannot corrupt terminal state.

### Penanggulangan

Test idle cancellation, running cancellation, FaceAnalysis cancellation, dependent jobs, reused/shared assets, restart, and duplicate reconciliation. The capability/readiness state must converge without waiting for the polling interval.

### Verification

Observe durable job cancellation and its dependent/capability projection in the same control flow or immediate signal-driven turn; a 750 ms timer must not be required for normal correctness.

### R4 implementation closure record — 2026-09-19

**Implementation SHA:** `36d1104d347e0312906d3832ecdacd9cd0d96fe8`  
**Primary remediation phase:** R4  
**Changed paths:** `JobScheduler.cs`; `JobCancellationOperations.cs`; `ProductionRuntimeRegistry.cs`.  
**Traceability checked:** running cancelled completion, idle cancellation, Stage2CompletionHandler, dependency cascade and reconciliation fallback.  
**Root-cause correction:** persisted CANCELLED invokes the terminal observer immediately for running completion and idle cancellation. Capability/dependency/readiness projection no longer depends on the periodic reconciliation interval in the normal path.  
**Regression guard:** observer failure cannot corrupt durable job truth; reconciliation remains the repair fallback.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static observer/wiring trace complete; timing execution remains R8.  
**Dependency findings checked:** X19, X25, X26, X68  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R8 projection timing matrix.

---

## X67 — Single-click Media Detail is intentionally delayed by the Windows double-click timeout

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

MediaActivationArbiter defers onSingleClick with Task.Delay(_doubleClickTimeMs). The value comes from GetDoubleClickTime() and falls back to 500 ms. This directly conflicts with the product interaction contract that a single click opens media preview/detail promptly while double click opens the media in the default external application.

Relevant path:

- src/Neuterradise.Runtime/Media/MediaGridViewModel.cs

### Root cause / failure path

The implementation treats single-click action as something that must be withheld until it can prove a double click will not happen. The single-click action is non-destructive and does not require that arbitration.

### Penyelesaian

Make the first unmodified left click immediately perform the internal selection/detail action.

Double click must then independently:

- preserve the already-valid internal selection/detail state;
- invoke the default-app action exactly once;
- not require retracting the first click;
- preserve modifier/selection semantics;
- keep route-generation, query-generation, scroll, and disposal fencing.

Do not replace the delay with a smaller arbitrary delay.

### Penanggulangan

Interaction tests:

- first click opens internal detail without waiting for GetDoubleClickTime;
- double click opens default app exactly once;
- modified clicks retain selection behavior;
- scrolling/navigation/disposal cannot trigger a stale action;
- keyboard Enter remains consistent with the internal-open contract.

### Verification

The first-click internal action occurs before the system double-click timeout expires, and the second click still produces exactly one external-open action.

---

## X68 — Shell work/status projection is fixed-polling at 750 ms

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

ProductionRuntimeRegistry.PublishStatusLoopAsync reads ShellWorkSnapshot on a PeriodicTimer of 750 ms. Durable scheduler/import state can therefore be correct while the visible shell counter/status remains stale for most of a second.

Relevant path:

- src/Neuterradise.Runtime/SystemServices/Lifecycle/ProductionRuntimeRegistry.cs

### Root cause / failure path

Visible projection refresh is timer-driven instead of change-driven. The scheduler already has wake/invalidation concepts, but shell status does not consume a direct durable-state change signal.

### Penyelesaian

Introduce an event-driven/coalesced status invalidation path.

- Scheduler/job/import state changes raise a lightweight wake/invalidation.
- The shell snapshot read is coalesced so bursts do not produce unbounded DB reads.
- Initial publication remains explicit.
- A bounded periodic timer may remain only as a missed-signal/recovery fallback.
- Cancellation/disposal must stop subscriptions deterministically.

### Penanggulangan

Test enqueue, start, progress/terminal state, cancel, retry, pause/resume, and import publication. Visible state must refresh from the signal path, not by sleeping until the next poll.

### Verification

A durable status change becomes observable without waiting 750 ms; disabling the fallback timer must not break the normal signal-driven path.

---

## X69 — Shared reused-asset Stage-2 work has no durable per-ImportUnit scheduling-interest model

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

Stage-2 work is Asset-owned and reused assets can be shared by multiple ImportUnits. JobWrites.SetImportUnitPriorityInTransactionAsync scopes focused/prioritized jobs only through import_items.candidate_asset_id. A unit waiting on reused_asset_id work can therefore remain dependent on a shared background-priority job even after the user prioritizes that import.

Relevant paths:

- src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs
- src/Neuterradise.Runtime/SystemServices/Jobs/ImportPriorityOperations.cs
- src/Neuterradise.Runtime/Import/Preparation/Stage2CompletionHandler.cs

### Root cause / failure path

The repository has Asset-owned shared jobs but no durable many-to-many concept expressing that multiple ImportUnits currently depend on or are interested in the same shared work.

Adding candidate_asset_id OR reused_asset_id blindly to cancellation SQL would be unsafe because one import must not cancel a shared job still needed by another import.

### Penyelesaian

Introduce one canonical shared-work interest/dependency authority.

It must support:

- ImportUnit → effective asset → shared job interest;
- focused-import priority aggregation without changing job ownership;
- per-unit progress/accounting for reused work;
- interest removal when a unit completes/cancels;
- shared job cancellation only when no live consumer remains and the job is otherwise cancel-safe;
- recovery/reconstruction after restart.

Priority policy must define how multiple consumers combine their desired priority deterministically.

### Penanggulangan

Two-or-more ImportUnits sharing the same reused asset:

- prioritize A while B remains background;
- prioritize B later;
- cancel A while B still needs the job;
- complete A while B remains;
- restart with both interests persisted;
- shared job failure/terminal projection reaches both units correctly.

### Verification

Focused import priority affects the shared work it actually waits on, without allowing one consumer to cancel or corrupt another consumer's job.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 2eded5f62b403b33b4fc2d4bf05738fc739fcf2b; 9c2e79edbfa7744dd02f7e1032dd2c1b01fc7bb4; 061a9de2e0795c38f7a3ccd3edec71adfd1110fa; 01c4e45e74536cf7a90ea45eaadd54a4819c08a5  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Database/Migrations/0011_r2_import_durability_authority.sql`; `src/Neuterradise.Runtime/Import/Preparation/Stage2PreparationCoordinator.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Reads/SchedulerReads.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportUnitWrites.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportWrites.cs`  
**Traceability checked:** effective Candidate/Reused Asset → durable `import_asset_interests` → shared job creation/focused-priority aggregation → live Pause/Resume → running-job cancellation eligibility → terminal/cancel-settlement priority recompute → interest release → restart reconciliation.  
**Root-cause correction:** shared Asset-owned Stage-2 work now has durable many-to-many ImportUnit interest. Priority is the deterministic maximum of live unpaused consumers; one paused/cancelled consumer cannot stop work still required by another; terminal/cancel-settled units release their rows.  
**Regression guard:** focus, pause/resume, cancellation, terminal completion, and restart all consume the same durable interest table and exclude terminal consumers from scheduling authority.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; multi-consumer scheduler execution remains R4/R8 integration evidence.  
**Dependency findings checked:** X66, X73  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R4 must revalidate scheduler-concurrency integration without reimplementing X69.



### R2 corrective closure record — 2026-09-19

**Reopen cause:** running-job cancellation had shared-consumer checks, but idle Asset jobs still used generic scoped cancellation and reused Assets were absent from the cancellation scope snapshot. Paused consumers also had to protect shared work from Cancel even though Pause itself may stop exclusive running work.  
**Corrective SHAs:** `4915d446219a44573fd03f0ee8ad163d422e2bf7`; `9279e8fb7789dfe4b089182578cfa2397c535e8c`  
**Corrective paths:** `JobWrites.cs`; `SchedulerReads.cs`; `ImportUnitWrites.cs`; `ImportUnitControlAuthority.cs`  
**Corrective authority:** idle Asset cancellation is now ImportUnit-aware and refuses to cancel while any other non-terminal candidate/reuse/interest consumer exists; cancellation-specific running-job selection uses the same global consumer authority and treats paused consumers as live protection; reused Assets are included in the cancelling unit's Asset scopes. Pause and Cancel deliberately use different running-work eligibility.  
**Revalidation:** one import can no longer cancel PENDING/RUNNABLE/PAUSED/FAILED_RETRYABLE or RUNNING shared work still owned by another live consumer.  
**Source status:** SOURCE-CLOSED (CORRECTED)  
**Runtime status:** NOT-YET-VERIFIED — multi-consumer scheduler execution remains R4/R8 evidence.

### R4 integration revalidation record — 2026-09-19

**Revalidated source SHA:** `1668aeaf231459b97bb2651a2d4325bbbfe32265` (latest R4 import-authority change: `2828b2b1eee8925b021ae557705a5ed12aa218ea`)  
**Primary remediation phase remains:** R2  
**R4 integration checked:** revalidated after the post-review Import control correction. Focus eligibility still resolves Asset work through durable `import_asset_interests`; Retry/Pause/Start/Clear-history now route through the corrected Import control authority without reintroducing candidate-only scheduling authority; terminal-state cleanup still recomputes shared priority from other live consumers before releasing the finishing unit's interest.  
**Source status:** SOURCE-CLOSED from R2; NO R4 REIMPLEMENTATION  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** shared-interest executable concurrency remains R8.

---

## X70 — Clipboard retry blocks the UI thread for up to approximately 200 ms

**Status:** CONFIRMED-SOURCE / OPEN-IMPLEMENTATION

### Temuan

Win32Clipboard.SetText retries OpenClipboard up to ten times and uses Thread.Sleep(20) between attempts. UI commands that call it synchronously can freeze the dispatcher for roughly 200 ms when another process holds the clipboard.

Relevant path:

- src/Neuterradise.Runtime/SystemServices/Win32Clipboard.cs

### Root cause / failure path

A bounded retry policy is implemented as synchronous sleeping on the caller thread, and UI commands are allowed to call the synchronous API directly.

### Penyelesaian

Provide a bounded non-blocking clipboard operation.

- no Thread.Sleep on the dispatcher;
- use asynchronous delay/backoff or an isolated worker appropriate to the Win32 clipboard contract;
- preserve a deterministic retry limit;
- support cancellation/route lifetime;
- surface final clipboard-busy failure without freezing the UI;
- serialize concurrent application clipboard writes if necessary.

### Penanggulangan

Simulate a locked clipboard while invoking Copy Path/Filename repeatedly. Verify dispatcher responsiveness, bounded completion, cancellation, and final error behavior.

### Verification

No UI-thread sleep occurs and the UI remains responsive for the full retry window.

---

## X71 — FFmpeg build artifact availability depends on an external upstream release

**Status:** CONFIRMED-HARDENING / SOURCE-CLOSED

### Temuan

package-win-x64.ps1 pins the FFmpeg archive name, release tag, SHA-256, source revision, and licenses, which protects byte identity. The canonical build still depends on the long-term availability of an external BtbN GitHub release URL when the local build cache is empty.

Relevant path:

- scripts/package-win-x64.ps1

### Root cause / failure path

Integrity is pinned, but artifact availability is not under the project's durable release authority. A deleted/retired/rate-limited upstream asset can make a clean canonical build impossible even though the expected bytes are known.

### Penyelesaian

Establish a durable approved artifact source for the exact pinned FFmpeg archive, such as a project-controlled release asset or immutable artifact store permitted by licensing.

Requirements:

- exact existing SHA-256 remains authoritative;
- provenance and upstream source commit remain recorded;
- licenses remain packaged;
- local cache remains usable;
- fallback ordering is deterministic;
- no automatic substitution with a newer FFmpeg build.

### Penanggulangan

Clean-cache build test using the durable source; upstream-unavailable simulation; hash mismatch fail-closed; provenance manifest comparison.

### Verification

The exact pinned FFmpeg bytes remain reproducibly obtainable from an authority controlled by the release process even if the original external URL is unavailable.

---


### R1 implementation closure record — 2026-09-19

**Implementation SHAs:** `f923926a62ac94654b754069d5f9457c38c33d36`, `f35a79d39b9b541fc60662222cccc2c1b73b0804`  
**Primary remediation phase:** R1  
**Changed paths:** `release-contract.json`; `scripts/package-win-x64.ps1`; project GitHub Release `dependencies-ffmpeg-8.1.2`.  
**Traceability checked:** pinned upstream tag/archive/SHA → project-controlled mirror URL → GitHub Release asset digest → cache hash authority → upstream fallback → extracted ffmpeg/ffprobe source-revision validation → deployment provenance.  
**Root-cause correction:** The exact pinned BtbN FFmpeg archive is now durably mirrored under the project release authority. Packaging remains mirror-first and accepts neither mirror nor upstream bytes unless they match the single pinned SHA-256.  
**Regression guard:** GitHub Release asset `ffmpeg-n8.1.2-51-g7ba069f4f1-win64-lgpl-8.1.zip` is uploaded with GitHub-reported digest `sha256:9bb4c17bf1e271e7944a61d900716e61c6f6b4ef556544f3907b131cbc0bfd41`; package cache and downloaded bytes are checked against that same digest, and no newer FFmpeg is substituted automatically.  
**Verification result:** GitHub Actions run `35443876832` SUCCESS published the asset. GitHub Release API read-back confirms state `uploaded`, size `146106441`, and the exact pinned SHA-256 digest.  
**Dependency findings checked:** X44, X50, X56  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** No R1 source/artifact blocker; clean-cache canonical package execution remains R8.
## X72 — REUSE authority is not revalidated at the commit boundary

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ImportCommitCoordinator collects items whose DuplicateDecision is Reuse, retires their candidate assets, records ReusedAssetId, marks cleanup LibraryCommitted, and later attempts LinkReusedMediaAsync. At that commit phase it does not first prove that the reused asset is still ACTIVE/non-trashed and still represents the exact duplicate authority reviewed for the candidate. Link failures are logged as warnings.

Relevant path:

- src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs

### Root cause / failure path

The REUSE decision is validated earlier, but the commit assumes that authority is still valid after time has passed and other lifecycle operations may have changed the reused asset.

review exact duplicate → reused asset changes/retires/trashes → commit retires candidate → reuse linking/authority is no longer valid.

### Penyelesaian

Immediately before DomainAuthorityCommitted, transactionally revalidate every reuse decision:

- reused asset exists;
- state is ACTIVE and not trashed;
- content identity still matches the reviewed candidate using the canonical exact-duplicate authority;
- required package/component identity is complete where applicable;
- ownership/relation policy still permits the intended destination semantics.

If revalidation fails:

- do not retire the candidate as successfully reused;
- do not mark the item LibraryCommitted;
- block/recompute the duplicate decision deterministically.

A required reuse association failure must not be reduced to a warning if it invalidates the commit contract.

### Penanggulangan

Race tests: trash/retire/replace/reassign the reused asset between Verify and commit; package component changes; row-version conflicts; restart at the revalidation boundary.

### Verification

A stale REUSE decision can never retire the only valid candidate and then silently commit against an invalid reused authority.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 8b12069f227dbd1dcf91397cfdaba30c7d112722; 061a9de2e0795c38f7a3ccd3edec71adfd1110fa  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportWrites.cs`; `src/Neuterradise.Runtime/Import/Verification/ImportCommitCoordinator.cs`  
**Traceability checked:** reviewed REUSE decision → whole-checkpoint prevalidation → ACTIVE managed Asset/package identity comparison → destination association → conditional Candidate retirement → domain checkpoint.  
**Root-cause correction:** every REUSE is revalidated before DomainAuthorityCommitted mutation, and Candidate retirement requires the reused Asset to remain ACTIVE with matching single-file identity or complete package bundle authority. A stale target blocks commit and preserves Candidate authority.  
**Regression guard:** missing/trashed/non-ACTIVE reused Asset, missing managed path, hash/length drift, package bundle drift, or incomplete discovery prevents DEDUP_REUSED retirement.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; concurrent REUSE mutation execution remains R8.  
**Dependency findings checked:** X11, X14, X69, X73  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** executable stale-REUSE concurrency matrix remains R8 evidence.



### R2 corrective closure record — 2026-09-19

**Reopen cause:** destination association was created before the final transactional REUSE revalidation/retirement, so a late authority failure could leave a MANUAL relation even though DomainAuthorityCommitted was blocked.  
**Corrective SHAs:** `b8e2721f103efe1c9de47f9229f489bf4fa450bc`; `4d90b70bedea340b063c0b26f3bb1102be1ff0e5`; `45f226f47c7e1ff924248a43108d52a61733acda`; `4841970a67bc4e3365abea8f627ff7c5b961f75c`  
**Corrective paths:** `ImportWrites.cs`; `ImportCommitCoordinator.cs`  
**Corrective authority:** final exact-duplicate revalidation, active managed reused-Asset authority, cancellation/Trash reservation exclusion, destination-profile validity, import-attributed MANUAL association, publication-delta proof, activity logging, Candidate retirement, and Candidate idle-job cancellation now commit in one Catalog transaction. The coordinator records REUSE state only after that transaction succeeds.  
**Revalidation:** reservation acquisition and REUSE are serialized by the write coordinator; a reserved/trashed/stale target returns failure before association/retirement, while any newly inserted MANUAL relation must carry this ImportUnit's `publication_import_unit_id` before Candidate retirement is allowed.  
**Source status:** SOURCE-CLOSED (CORRECTED)  
**Runtime status:** NOT-YET-VERIFIED — concurrent stale-REUSE mutation execution remains R8 evidence.


---

## X73 — Cancelling one import can trash an asset currently reused by another import

**Status:** CONFIRMED-SOURCE / SOURCE-CLOSED

### Temuan

ImportCancellationSettlement.ReadExclusiveActiveAssetIdsAsync selects ACTIVE candidate assets for the cancelled unit and excludes reused_asset_id only from import_items belonging to that same unit. It does not prove that another live ImportUnit currently references/reuses that asset.

Relevant path:

- src/Neuterradise.Runtime/Import/ImportCancellationSettlement.cs

A concrete failure is:

Import A creates Asset X → Import B reuses X → A is cancelled → A's exclusivity query sees no same-unit reuse → X is considered exclusive → cancellation settlement attempts to trash X while B still depends on it.

### Root cause / failure path

Asset lifetime/exclusivity is inferred from one ImportUnit's rows instead of from all live consumers and durable library relations.

### Penyelesaian

Define canonical asset-retirement eligibility for cancellation.

An asset created by the cancelled unit may be retired/trashed only when all of the following are proven transactionally:

- no other live ImportUnit has candidate/reuse/effective-asset interest requiring it;
- no published/active Profile relation requires it;
- no active shared-work consumer depends on it;
- no lifecycle operation has transferred durable authority;
- the asset is still eligible for rollback under the cancelled unit's publication delta.

Integrate this authority with X69's shared-work interest model. Do not fix this by merely changing a same-unit subquery to candidate OR reused.

### Penanggulangan

Concurrent import matrix:

- A creates X, B reuses X, cancel A;
- cancel B first;
- publish B then cancel A;
- pause/restart both;
- three consumers;
- consumer appears during cancellation settlement;
- relation/owner changes during rollback.

### Verification

Cancelling one ImportUnit can never retire/trash an asset that another live import or active library relation still requires.


### R2 implementation closure record — 2026-09-19

**Implementation SHA:** 9c2e79edbfa7744dd02f7e1032dd2c1b01fc7bb4; 061a9de2e0795c38f7a3ccd3edec71adfd1110fa; 01c4e45e74536cf7a90ea45eaadd54a4819c08a5  
**Primary remediation phase:** R2  
**Changed paths:** `src/Neuterradise.Runtime/Import/ImportCancellationSettlement.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Reads/SchedulerReads.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportUnitWrites.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/ImportWrites.cs`; `src/Neuterradise.Runtime/SystemServices/Database/Writes/JobWrites.cs`  
**Traceability checked:** cancelled unit → running shared-work safe boundary → global ImportItem/effective-asset consumers → durable shared interests → active Profile relations → exclusive rollback eligibility → Trash → DB delta cleanup → rollback settlement/interest release.  
**Root-cause correction:** cancellation no longer infers exclusivity from same-unit rows. An ACTIVE Asset is rollback-trash eligible only when no other live ImportUnit/effective-asset interest and no external active Profile relation requires it. Shared running jobs are not signalled/cancelled while another live consumer exists.  
**Regression guard:** A-create/B-reuse, multiple consumers, paused/restarted consumers, published relations, and shared running-job queries all use global durable consumer authority before destructive rollback.  
**Verification result:** PENDING-EXECUTION-AUTHORIZATION — static source/traceability review completed; concurrent cancellation/Trash execution remains R4/R8 integration evidence.  
**Dependency findings checked:** X06, X07, X08, X66, X69, X72  
**Source status:** SOURCE-CLOSED  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** R4 must revalidate scheduler/cancellation concurrency; R8 owns executable Trash/cancel evidence.



### R2 corrective closure record — 2026-09-19

**Reopen cause:** exclusivity was initially read before Trash preparation/commit, leaving a TOCTOU window in which a second consumer or durable relation could appear; same-destination relations also needed attribution-based, not merely profile-id-based, ownership logic.  
**Corrective SHAs:** `4915d446219a44573fd03f0ee8ad163d422e2bf7`; `b8e2721f103efe1c9de47f9229f489bf4fa450bc`; `9279e8fb7789dfe4b089182578cfa2397c535e8c`; `4d90b70bedea340b063c0b26f3bb1102be1ff0e5`  
**Corrective paths:** `ImportCancellationSettlement.cs`; `ImportUnitWrites.cs`; `TrashCoordinator.cs`; migrations `0012_r2_shared_rollback_reservation.sql` and `0013_r2_cancel_asset_reservation.sql`  
**Corrective authority:** cancellation first acquires a unique durable `import_cancel_asset_reservations` row in the same serialized write transaction that proves the Asset is still ACTIVE, belongs to the cancelled unit, has no other live candidate/reuse/interest consumer, and has no published or foreign import-attributed Profile relation. Reservation triggers prevent new interests, REUSE selection, or Profile relations until rollback settlement. Rollback-specific Trash revalidates eligibility before physical movement and again inside the final Trash DB transaction. If another authority wins before reservation, the Asset is preserved and cancellation removes only its own unpublished delta.  
**Revalidation:** the A-creates-X/B-reuses-X race, paused consumer, new relation, and consumer-appears-during-settlement windows now have one serialized winner instead of read-then-trash TOCTOU. Reservations are released only with `rollback_settled=1`.  
**Source status:** SOURCE-CLOSED (CORRECTED)  
**Runtime status:** NOT-YET-VERIFIED — concurrent cancellation/Trash execution remains R4/R8 evidence.

### R4 integration revalidation record — 2026-09-19

**Revalidated source SHA:** `1668aeaf231459b97bb2651a2d4325bbbfe32265` (latest R4 import-authority change: `2828b2b1eee8925b021ae557705a5ed12aa218ea`)  
**Primary remediation phase remains:** R2  
**R4 integration checked:** revalidated after the post-review Import control correction. Per-unit serialization, admitted Retry/control commands, and terminal-interest cleanup do not weaken R2 cancellation reservations/exclusivity. Shared Asset consumer evidence continues to prevent another live import's reused Asset from being classified as exclusive rollback material.  
**Source status:** SOURCE-CLOSED from R2; NO R4 REIMPLEMENTATION  
**Runtime status:** NOT-YET-VERIFIED  
**Residual risk/blocker:** multi-import cancellation execution remains R8.

---

# 19. Historical current-audit crosswalk

The pre-stage deep-source register D38–D57 is preserved below so no confirmed finding can disappear because the audit numbering scheme changed.

| Historical ID | Canonical X ID | Finding |
| --- | --- | --- |
| D38 | X64 | CodeQL Zip Slip / explicit extraction containment proof |
| D39 | X25 | Optional capability incorrectly gates import readiness |
| D40 | X65 | Canonical ZIP directory-entry incompatibility |
| D41 | X43 | NuGet global-packages root mismatch |
| D42 | X66 | Cancellation terminal projection delay |
| D43 | X67 | Single-click delayed by double-click timeout |
| D44 | X68 | Shell status fixed polling |
| D45 | X69 | Shared/reused Stage-2 per-import interest missing |
| D46 | X70 | Clipboard UI blocking retry |
| D47 | X50 | Exact .NET SDK / deterministic toolchain authority |
| D48 | X71 | FFmpeg artifact durability |
| D49 | X56–X61 | Build/package/runtime executable evidence gap |
| D50 | X45 | Updater publisher authenticity |
| D51 | X02 | Updater staging-to-handoff TOCTOU |
| D52 | X10 | MOVE source pathname/object deletion race |
| D53 | X06 | Asset Trash with APPEARS/MANUAL |
| D54 | X72 | REUSE authority commit-time revalidation |
| D55 | X73 | Cross-import cancellation can trash shared reused asset |
| D56 | X01 | VIDEO Cover rejected by CriticalIntegrityGate |
| D57 | X07–X08 | NORMAL Profile Trash and paired restart/restore lifecycle |

This crosswalk is normative. Historical D IDs are aliases only; implementation and closure tracking use X IDs.

---

# 20. Frozen-tree coverage certification — 388/388 baseline files

The frozen SHA 3f5f7c861fd773336182e4c61871e95c8761a549 contains exactly **388 files**. The current tree before this documentation series had zero file differences from that SHA. The only extra current file is this audit document.

Every baseline area below has an owning audit stage. A row without a unique finding does not mean it was skipped; it means the area was traced and did not produce a separate confirmed defect beyond the listed cross-domain findings.

| Baseline area | Files | Owning coverage |
| --- | ---: | --- |
| Root solution/build scaffolding | 5 | Stages 1, 9, 11, 12; X50, X56–X61 |
| .github/workflows | 2 | Stages 1, 9, 11; X46, X56 |
| scripts | 3 | Stages 1, 9, 11, 12; X43–X50, X65, X71 |
| Neuterradise.App root/platform/assets/UI | 28 | Stages 1, 8, 10, 11; X38–X42, X67–X70 |
| Profiling.Protocol | 4 | Stages 1, 6, 10, 11; X30–X36 |
| Profiling.Worker | 16 | Stages 1, 6, 10, 11; X03, X30–X36, X59 |
| Runtime/Activity | 3 | Stages 1, 8, 10, 11; no unique additional finding |
| Runtime/Design | 16 | Stages 1, 8, 10, 11; no unique additional finding |
| Runtime/Faces | 8 | Stages 1, 6, 7, 8, 10, 11; X30–X36 |
| Runtime/Gallery | 2 | Stages 1, 8, 10, 11; X38–X42 where applicable |
| Runtime/Home | 1 | Stages 1, 8, 10, 11; no unique additional finding |
| Runtime/Import | 38 | Stages 1, 4, 5, 10, 11, 12; active X09–X14, X17, X19–X26, X69, X72–X73; rejected-evidence ownership X15–X16 and X18 |
| Runtime/Localization | 4 | Stages 1, 8, 10, 11; X40 |
| Runtime/Maintenance | 10 | Stages 1, 3, 4, 7, 10, 11; X13 and lifecycle integrity coverage |
| Runtime/Media | 24 | Stages 1, 4, 8, 10, 11, 12; active findings X09–X14, X17, X42, X67, X70 |
| Runtime root project/global usings | 2 | Stages 1, 9, 11; build/toolchain coverage |
| Runtime/Presentation | 12 | Stages 1, 8, 10, 11; presentation install/runtime traced; no unique additional finding |
| Runtime/Profiles | 10 | Stages 1, 2, 3, 7, 8, 10, 11; X01, X04–X08, X37–X42 |
| Runtime/RelatedProfiles | 5 | Stages 1, 6, 7, 8, 10, 11; related-evidence lifecycle traced |
| Runtime/Settings | 4 | Stages 1, 8, 10, 11; X39–X41 |
| Runtime/Shell | 15 | Stages 1, 8, 10, 11, 12; X38–X42, X53–X55, X68 |
| SystemServices/Cache | 15 | Stages 1, 4, 6, 8, 10, 11; cache/resource lifetime traced |
| SystemServices/Database | 49 | Stages 1, 3–7, 10, 11; active findings X06–X08, X19–X26, X37, X69, X72–X73 |
| SystemServices/Diagnostics | 2 | Stages 1, 10, 11; runtime evidence support |
| SystemServices/Jobs | 27 | Stages 1, 5, 6, 10, 11, 12; active findings X19–X26, X30–X36, X66, X69 |
| SystemServices/Lifecycle | 9 | Stages 1, 2, 5, 10, 11, 12; X01, X22–X23, X52–X55, X68 |
| SystemServices/MediaTools | 4 | Stages 1, 4, 9, 11, 12; X44, X57, X71 |
| SystemServices/Operations | 6 | Stages 1, 3–5, 7, 10, 11; idempotency/operation authority coverage |
| SystemServices/ProductIdentity.cs | 1 | Stages 1, 9, 11; X49–X50 |
| SystemServices/Recovery | 8 | Stages 1, 3–5, 7, 9–11; active findings X08, X23, X47–X48, X52–X55 |
| SystemServices/Resources | 2 | Stages 1, 5, 8, 10, 11; resource-governor/lifetime coverage |
| SystemServices/Storage | 23 | Stages 1, 4, 7, 9–11; active findings X04–X14, X17, X37, X52 |
| SystemServices/TimeAndIds | 2 | Stages 1, 3, 5, 10; identity/time durability support |
| SystemServices/UiPrimitives.cs | 1 | Stages 1, 8, 10; UI primitive coverage |
| SystemServices/Updates | 14 | Stages 1, 9–12; active/evidence findings X02, X43–X50, X55, X60, X64–X65 |
| SystemServices/Win32Clipboard.cs | 1 | Stages 1, 8, 12; X70 |
| Runtime/Trash | 9 | Stages 1, 3, 7, 10, 11; X06–X08, X37 |
| Neuterradise.Updater | 3 | Stages 1, 9–11; X02, X45–X49, X55, X60 |

**Total: 388 / 388 baseline files owned by the audit map.**

## 20.1 Cross-cutting failure-mode coverage

Coverage is not based only on directory ownership. The audit explicitly traced these failure dimensions end-to-end:

| Failure dimension | Owning findings/stages |
| --- | --- |
| DB trigger/FK invariant conflict | X06–X08, X37; Stages 3 and 7 |
| crash between physical I/O and DB checkpoint | X08–X12, X37, X47, X52–X55 |
| cancellation/pause/shutdown intent races | X19–X26, X52–X55, X66, X69, X73 |
| stale writer / CAS / terminal-state escape | X21, X24 |
| shared asset / cross-import lifetime | X69, X72–X73 |
| path traversal / containment / reparse | X64–X65 (X18 rejected by falsification) |
| destructive source deletion identity | X10–X11 |
| package/component completeness | X11, X13–X17, X44, X65 |
| worker IPC/model/native runtime | X03, X30–X36, X59 |
| UI dispatcher / stale callback / perceived latency | X38–X42, X67–X70 |
| updater trust / TOCTOU / recovery | X02, X45–X49, X55, X60, X64–X65 |
| build/dependency reproducibility | X43–X44, X46, X50, X56–X61, X71 |
| cold/warm startup / controlled shutdown | X22–X23, X52–X55, X58, X61 |
| executable evidence versus static inference | X56–X61 |

## 20.2 Baseline re-audit freeze

For the unchanged frozen source baseline, this coverage certification is the closure mechanism.

Do **not** schedule another full source audit of 3f5f7c8 or its net-identical source tree merely to seek reassurance. Implementation work must proceed against this ledger. During remediation:

- a manifestation of an existing root cause is attached to the existing X finding;
- a regression caused by a fix is treated as a failed verification of that finding/cluster;
- only a genuinely distinct root cause outside X01–X73 may allocate X74+;
- executable verification is mandatory but is not another broad source audit.

No static audit can mathematically prove that software contains zero bugs. The concrete closure claim here is narrower and testable: **there is no intentionally unowned repository domain, no lost confirmed current-audit finding, and no omitted planned failure-mode class in the Stage 1–12 coverage map.**

---

# 21. Dependency clusters

Findings must not be fixed as unrelated tickets when they share one invariant.

## Cluster A — Appearance and profile authority

X01, X06–X08, X37, X38, X42

Shared contract:

- valid profile graph is defined once;
- lifecycle transitions close identity/relation/appearance authority;
- UI projection never outruns durable state;
- restart sees the same valid graph.

## Cluster B — Managed path / physical authority

X04, X05, X09–X14, X17, X72

Shared contract:

- path intent is durable before physical mutation;
- collision authority is canonical;
- component/package membership is explicit;
- destructive cleanup is object-safe;
- crash replay is idempotent.

## Cluster C — Scheduler / command / shutdown

X19–X26, X52–X55, X66, X69, X73

X51 is REJECTED and is not part of this remediation cluster.

Shared contract:

- explicit control intent;
- monotonic terminal state;
- one mutation lease;
- admission closes before quiescence;
- writers stop before VaultLock release;
- interrupted resumable work remains reclaimable.

## Cluster D — Faces / Worker

Source findings: X03, X30–X36.  
Verification companion: X59.

Shared contract:

- one package/runtime model root;
- verified input;
- correct YuNet/SFace provenance;
- responsive cancellation;
- bounded worker restart;
- bounded/chunked IPC;
- deterministic cache/index cleanup;
- packaged inference verification.

## Cluster E — UI persistence and lifetime

X38–X42, X67–X70

Shared contract:

background read/compute → durable mutation where required → UI dispatcher commit → generation/lifetime check.

## Cluster F — Release trust and updater lifecycle

Source/hardening findings: X02, X43–X50, X55, X64, X71.  
Verification companions: X57, X60, X65.

Shared contract:

signed/pinned authority → package completeness → compatibility → staged content revalidation → replacement → recovery → verified terminal cleanup.

---

# 22. Direct remediation order

The following order minimizes rework and prevents fixing symptoms before their authority layer.

**Primary-phase rule:** every CONFIRMED-SOURCE or CONFIRMED-HARDENING finding has exactly one primary remediation phase. A finding may be named again in a later phase only as an **integration/revalidation companion**; that later appearance does not authorize a second independent implementation, reset closure status, or require a new closure SHA. The original finding body and its closure record remain the sole status authority.

## Phase R0 — Freeze audit authority

- Commit and retain this file.
- Do not renumber the ledger.
- Do not open Stage 13 for this baseline.
- New source defects discovered during implementation use X74+ only if they are genuinely outside the existing finding root cause.

Acceptance: all agents use this document as the audit authority.

## Phase R1 — Fix root authority contradictions

Target:

- X01;
- X02;
- X03;
- X04;
- X05;
- X43;
- X44;
- X49;
- X50;
- X64;
- X71.

X65 is an execution-evidence task and belongs to R8, not source remediation unless reproduced.

**R1 implementation status — 2026-09-19:** SOURCE-CLOSED. X01, X02, X03, X04, X05, X43, X44, X49, X50, X64, and X71 are SOURCE-CLOSED. X50 now has six authentic SDK-10.0.401 NuGet lockfiles plus locked canonical restore/provenance; X71 now has the exact pinned FFmpeg archive under the project-controlled GitHub Release authority. Build/package/runtime acceptance remains R8 and is not implied by R1 source closure.

Reason: later fixes depend on stable appearance, deployment, model, path-job, package, compatibility, and build authorities.

Do not start broad runtime acceptance until these authorities are coherent.

## Phase R2 — Fix storage/import durability

**Implementation status:** SOURCE-CLOSED after corrective revalidation through `4841970a67bc4e3365abea8f627ff7c5b961f75c`. The earlier `acb8ed0613243b19570d37213425de25f2e4d239` ledger closure was reopened by source review for X10, X12, X14, X69, X72, and X73; the corrective records in those findings are authoritative. Executable verification remains owned by R4 integration companions and R8; this source-closure statement does not claim runtime verification.

Target:

- X09–X14;
- X17;
- X69;
- X72;
- X73.

Do not implement X15, X16, or X18; they are rejected by Section 4A falsification.

Implement as a coordinated storage/import pass, not ten isolated local patches.

Acceptance:

- exact replay authority;
- source deletion fail-closed;
- package/component authority durable;
- cancellation/compensation complete;
- health/dependency/duplicate semantics package-aware;
- collision and containment canonical.

## Phase R3 — Fix Trash/Profile lifecycle

**Implementation status — 2026-09-19:** SOURCE-CLOSED after corrective revalidation through `d6063d8392bfbe522747f92a43767a71566b8cf6`. Initial R3 source landed in `c82ab6997dbdd9155bff41acdfcad3b5a465eae4` → `0b8d6e1ef14152107db1e21c7c4aecd12d548454` → `0e0aa2bc2c4218146f3a548bc1175904efe68220`. Review then found remaining Restore/Purge mutual-exclusion, post-DB manifest recovery, manifest-identity replay, legacy checkpoint, and X37 policy gaps. Corrective source landed in `5648fccbcaa8953e0601b2d9c17511c4321d14ef` → `d6063d8392bfbe522747f92a43767a71566b8cf6`. X06, X07, X08, and X37 are SOURCE-CLOSED on the corrective source. Runtime/fault-injection acceptance remains R8 and is not implied by this source closure.

Target:

- X06;
- X07;
- X08;
- X37.

Acceptance:

- DB triggers/FKs remain enabled;
- Trash/Restore/Purge converge under crash injection;
- no physical recovery evidence is destroyed before final dependency closure.

## Phase R4 — Fix scheduler and lifecycle concurrency

**Implementation status — 2026-09-19:** SOURCE-CLOSED after post-implementation review and corrective fixes through `1668aeaf231459b97bb2651a2d4325bbbfe32265` (Import authority correction `2828b2b1eee8925b021ae557705a5ed12aa218ea`; dispatcher lifetime correction `1668aeaf231459b97bb2651a2d4325bbbfe32265`). The earlier source-closing SHA `469eee8b14609032cee280f9bf060d858dc98550` is retained as historical provenance but is superseded for X20/X21/X22/X24/X54 correctness. Security Scan completed SUCCESS for both corrective source commits `2828b2b1eee8925b021ae557705a5ed12aa218ea` and `1668aeaf231459b97bb2651a2d4325bbbfe32265`; this is static/security evidence only, not runtime acceptance. Primary findings X19–X26, X52–X55, and X66 are SOURCE-CLOSED at source level; R2-owned companions X69/X73 were revalidated against the corrected R4 source without duplicate implementation and X51 remains REJECTED. Build/test/runtime/fault-injection acceptance is still not claimed; executable evidence remains R8 under AGENTS.md.

Primary target:

- X19–X26;
- X52–X55;
- X66.

Integration/revalidation companions already owned by R2:

- X69;
- X73.

Do not reimplement X69/X73 in R4. Revalidate only their scheduler/cancellation integration after the R4 authority changes.

Do not implement X51; it is rejected by Section 4A falsification.

Required first-class concepts:

- JobControlIntent;
- per-unit mutation lease;
- monotonic CAS transitions;
- process-wide mutation admission gate;
- reverse-order startup rollback;
- shared shutdown/update deadline.

Acceptance:

- no stale terminal escape;
- no unclaimable RUNNABLE jobs;
- no new command after ShuttingDown;
- VaultLock released last.

## Phase R5 — Fix profiling/faces

**Implementation status — 2026-09-19:** SOURCE-CLOSED through `2c1ebd71abe0e7b65055d6d54709dae34b6c72e3` (protocol/worker foundation `36c0ef512a5a347033a28025bf6641f15a2fbb24`; runtime/persistence closure `fd4a5dd7a1e647cb6a646906fffc431d4228691c`; direct-sample cache hardening `f2a6d5bf858fef03cd594d193f02f1325c7d9d9a`). X30–X36 are SOURCE-CLOSED at source level. No build/test/native runtime/fault-injection acceptance is claimed here; executable evidence remains R8 under AGENTS.md.

Target:

- X30–X36.

Dependency note: X03 remains primary R1 only. R5 begins after X03 is SOURCE-CLOSED because Worker model topology is an upstream authority; this dependency does not make X03 an R5 target.

Acceptance:

- active cancellation works;
- hash mismatch cannot infer;
- identity cache freshness;
- correct model provenance;
- breaker limits crash loop;
- large bank transport works;
- no leaked worker indexes.

## Phase R6 — Fix UI threading/persistence/lifetime

Target:

- X38–X42;
- X67;
- X68;
- X70.

Acceptance:

- dispatcher affinity;
- persistence-before-live commit where required;
- stale generation fencing;
- no partial hydration completion.

## Phase R7 — Complete release/update hardening

Primary target:

- X45–X48.

Integration/revalidation companions already owned by earlier phases:

- X02, X43, X44, X49, X50, X64, X71 — primary R1;
- X55 — primary R4.

Do not reimplement those companion findings in R7. Revalidate the complete trust/update chain after X45–X48 land. X65 remains an R8 execution-evidence task unless reproduced and formally reclassified under its state machine.

Acceptance:

- independent publisher authenticity;
- least-privilege workflow;
- typed recovery;
- terminal cleanup;
- full manifest trust chain.

## Phase R8 — Build and executable verification

Target:

- X56–X61;
- X65.

This phase is mandatory for final audit acceptance after source remediation, but **this document does not override AGENTS.md's execution restriction**. Until the user/maintainer explicitly authorizes build/test/application execution, R8 remains **PENDING-EXECUTION-AUTHORIZATION** and agents must not launch the application or run tests merely because this section exists.

Once explicit execution authorization is given, R8 is the canonical verification scope; agents must use the repository-owned commands/harnesses and the isolated/disposable roots defined here rather than inventing alternate verification paths.

Order:

1. exact-SHA canonical Release build;
2. package self-validation;
3. disposable package extraction;
4. cold/warm startup;
5. database bootstrap/recovery;
6. packaged Worker E2E;
7. import lifecycle matrix;
8. Trash/Profile lifecycle matrix;
9. updater replacement/recovery matrix;
10. controlled shutdown/restart;
11. final regression gate.

A build PASS does not close runtime findings.

---

# 23. Implementation discipline

For each source finding:

1. Read the finding, its Section 4B trace target, and every finding in its dependency cluster.
2. Confirm the listed source paths/symbols still exist on the implementation SHA, then trace all current callers before editing. If authority moved, update Section 4B in the same commit.
3. Work only in the finding's primary remediation phase; later integration-phase references do not authorize duplicate implementation.
4. Change the canonical authority rather than adding a second authority.
5. Preserve existing safety constraints unless the finding explicitly proves they are contradictory.
6. Add the regression guard in the same implementation unit.
7. Verify failure/recovery paths, not only happy path.
8. Record the commit/SHA that closes the finding.
9. Do not mark SOURCE-CLOSED when only a symptom is patched.
10. Do not delete recovery evidence merely to make a test pass.
11. Do not disable triggers/FKs/integrity checks to make lifecycle operations succeed.
12. Do not add alternate build/package/update paths.
13. Do not touch the real Vault during build/package/release verification.

Repository operating rules from AGENTS.md remain authoritative for canonical build/release commands and cache/Vault handling.

---

# 24. Finding closure record format

When implementation begins, each finding entry should append a closure record using this exact structure:

**Implementation SHA:** exact commit  
**Primary remediation phase:** R1–R7 owner from Section 22  
**Changed paths:** exhaustive intended paths  
**Traceability checked:** Section 4B paths/symbols plus any newly discovered callers  
**Root-cause correction:** what authority/state machine was changed  
**Regression guard:** exact automated guard  
**Verification result:** command/harness and result, or PENDING-EXECUTION-AUTHORIZATION when AGENTS.md prohibits execution absent explicit instruction  
**Dependency findings checked:** IDs  
**Source status:** SOURCE-CLOSED or still OPEN-IMPLEMENTATION  
**Runtime status:** VERIFIED/RUNTIME-VERIFIED only when actually executed  
**Residual risk/blocker:** none or exact blocker

Never replace the original Temuan/Penyelesaian/Penanggulangan text with only the closure record. The reason for the fix must remain visible.

---

# 25. Final acceptance criteria

NeuTerradise may be described as remediated against this Deep Audit only when all of the following are true:

1. Every CONFIRMED-SOURCE and CONFIRMED-HARDENING record is SOURCE-CLOSED (or evidence-backed superseded), every EXECUTION-EVIDENCE-GAP is resolved by the required execution evidence, any disproved evidence hypothesis is explicitly RESOLVED-NOT-REPRODUCED, and REJECTED records remain excluded from implementation.
2. X62–X63 remain CLOSED-DOC audit-control records.
3. X27–X29 remain RESERVED.
4. All DB trigger/FK invariants remain active.
5. All finding-specific regression guards pass.
6. Canonical exact-SHA Release build passes.
7. Package integrity passes.
8. Extracted package starts.
9. Worker/model E2E passes.
10. Import COPY/MOVE/reuse Pause/Resume/Cancel/restart passes.
11. Asset Trash/Restore/Purge passes.
12. Profile Trash/Restore/Purge passes.
13. startup failure and shutdown races preserve VaultLock authority.
14. updater normal, abort, crash, restore, corruption, and cleanup scenarios converge.
15. no verification uses a real user Vault.
16. final build/package/runtime evidence is tied to the same intended fixed source SHA.

Until then:

**Deep Audit = CLOSED-DOC.**  
**Remediation = OPEN.**  
**Production/runtime acceptance = NOT YET PROVEN.**

---

# 26. Final audit decision

The source baseline has been audited through all twelve planned stages. The final coverage certification did not reveal a new repository domain requiring a Stage 13, but it did reveal a ledger-consolidation defect: ten already-confirmed current-audit findings from historical D38–D57 had not been represented explicitly in the X register. Those findings are now restored canonically as X64–X73, and the D38–D57 crosswalk is frozen in this document.

The frozen baseline is therefore covered at two levels:

1. **388/388 file ownership** across Stage 1–12; and
2. **failure-mode ownership** across database invariants, filesystem crash boundaries, concurrency, shared assets, UI threading/lifetime, profiling IPC, updater trust/recovery, build reproducibility, and executable runtime evidence.

The documentation itself has also been hardened against remediation drift: Section 4B supplies a complete X01–X73 traceability index; Stage 5 has an explicit canonical heading; X62/X63 now expose their audit-control causes; X65 has an explicit evidence-to-source state machine; every active finding has one primary remediation phase; integration-only repeats are labeled; and R8 execution authority is reconciled with AGENTS.md.

The correct next action is implementation against this document, not another full audit of the same unchanged source tree.

If implementation reveals a genuinely distinct defect outside the root causes of X01–X73, allocate X74 and continue monotonically. If an observation is another manifestation of an existing root cause, attach it to that existing finding instead of creating a duplicate.

**Canonical conclusion: Deep Audit Stage 1–12 is TRUST-CERTIFIED at the static-source level after falsification of the historical ledger. Remediation and executable verification remain open. Full source re-audit of the unchanged frozen baseline is not required; implementation must follow the classifications in Section 4A.**
