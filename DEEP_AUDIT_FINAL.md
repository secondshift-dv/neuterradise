# NeuTerradise — Canonical Deep Audit Final

## Stage 1–12 Closure, Remediation Plan, and Regression Prevention Contract

**Repository:** secondshift-dv/neuterradise  
**Canonical branch:** main  
**Audit date:** 2026-09-19  
**Frozen source baseline:** 3f5f7c861fd773336182e4c61871e95c8761a549  
**Pre-documentation live HEAD:** be5d99b4659892de1b8d2697538fd6e29c3c24b5  
**Pre-documentation comparison:** main is two commits ahead of the frozen baseline with zero file differences. The two commits are an unintended Stage 4 source change and its revert.  
**Authority status:** This file is the canonical Deep Audit Stage 1–12 ledger and remediation specification.  
**Audit status:** CLOSED-DOC  
**Source remediation status:** OPEN  
**Build/package/runtime acceptance:** NOT YET PROVEN  
**Next valid finding ID:** X74

---

# 1. Purpose

This document exists to stop audit drift.

The Deep Audit is finished for the frozen source baseline. The next phase is not another broad audit. The next phase is implementation of the findings in this document, followed by exact regression and runtime verification.

Every concrete finding below has four mandatory layers:

1. **Temuan** — the actual defect or evidence gap.
2. **Root cause / failure path** — why the defect exists and how the failure propagates.
3. **Penyelesaian** — the direct corrective design that must be implemented.
4. **Penanggulangan** — the permanent regression guard, invariant, recovery rule, or verification gate that prevents the same class of defect from returning.

A finding is not source-closed merely because a local symptom was patched. Closure requires the root cause, sibling paths, recovery behavior, and regression guard to converge.

---

# 2. Status semantics

The following status vocabulary is mandatory.

- **CLOSED-DOC** means the audit investigation is complete enough to define the defect, root cause, solution, and prevention contract.
- **OPEN-IMPLEMENTATION** means the source or verification work described by the finding has not yet been accepted as corrected.
- **SOURCE-CLOSED** may be assigned only after the corrective implementation and required regression guards are committed.
- **VERIFIED** may be assigned only after the exact required verification succeeds on the intended source tree or package.
- **RUNTIME-VERIFIED** may be assigned only after executable runtime behavior is proven, not inferred from source inspection.
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
8. New findings after this document start at X64.
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

Concrete documented findings: **70**.  
- **68 remediation/evidence findings:** X01–X26, X30–X61, X64–X73.  
- **2 audit-control findings:** X62–X63.  
Reserved historical IDs: **3**.

---

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

**Status:** OPEN-IMPLEMENTATION

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

## X02 — Updater helper loses manifest/hash authority after handoff

**Status:** OPEN-IMPLEMENTATION

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

## X03 — Packaged profiling model root disagrees with Worker resolver

**Status:** OPEN-IMPLEMENTATION

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

## X04 — Profile rename reconciliation obligations lack guaranteed production enqueue

**Status:** OPEN-IMPLEMENTATION

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

## X05 — OWNER relocation obligations lack guaranteed production enqueue

**Status:** OPEN-IMPLEMENTATION

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

# 7. Stage 3 — Database / Persistence / Lifecycle Findings

## X06 — Asset Trash fails when APPEARS or MANUAL relations remain

**Status:** OPEN-IMPLEMENTATION

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

---

## X07 — NORMAL Profile Trash violates active identity/relation invariants

**Status:** OPEN-IMPLEMENTATION

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

---

## X08 — Profile Trash/Restore is not restart-convergent

**Status:** OPEN-IMPLEMENTATION

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

---

# 8. Stage 4 — Import / Media / Vault / Storage Findings

## X09 — Same-volume replay misclassifies an operation-owned target as UnexpectedTarget

**Status:** OPEN-IMPLEMENTATION

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

---

## X10 — MOVE source deletion is fail-open when stable object identity is unavailable

**Status:** OPEN-IMPLEMENTATION

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

---

## X11 — Imported model/package component cleanup uses incomplete source authority

**Status:** OPEN-IMPLEMENTATION

### Temuan

Multi-component imported assets require per-component source→managed mapping. Cleanup cannot safely reconstruct every component source merely from the primary source directory or target package layout.

### Root cause / failure path

The primary asset is treated as the whole cleanup authority even when the imported object is a package.

### Penyelesaian

Persist exact per-component cleanup obligations:

- original source path;
- relative component identity;
- expected length/hash;
- stable source identity;
- managed target authority;
- cleanup state.

Do not infer a source path later when exact authority can be persisted during admission/materialization.

### Penanggulangan

Regression matrix:

- included package;
- reused asset;
- nested component paths;
- renamed source package;
- component mismatch;
- partial deletion;
- retry/restart;
- row-version/CAS conflict.

### Verification

Package cleanup must prove every destructive component delete independently.

---

## X12 — Pre-domain side effects are not fully compensation- and cancellation-fenced

**Status:** OPEN-IMPLEMENTATION

### Temuan

Import/storage preparation can perform filesystem or staging side effects before the final domain authority is committed. Cancellation or failure in the gap can leave durable residue not owned by a terminal domain state.

### Root cause / failure path

Side effects precede the authority/checkpoint that explains them, without one compensation owner.

### Penyelesaian

Every pre-authority side effect must have one of:

- an operation-scoped durable journal written first;
- a deterministic idempotent cleanup obligation;
- a rollback/compensation phase;
- a cancellation fence that prevents terminal cancellation until the side effect is checkpointed.

### Penanggulangan

Crash/cancel before and after every directory creation, copy, intermediate move, package extraction, and checkpoint.

No orphan physical object may exist without a durable operation that can reconcile it.

### Verification

Recovery sweep after injected process termination must classify and converge every residue.

---

## X13 — Health/integrity evaluation is package-unaware

**Status:** OPEN-IMPLEMENTATION

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

---

## X14 — Dependency discovery can fail open

**Status:** OPEN-IMPLEMENTATION

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

---

## X15 — Duplicate/reuse authority can split primary asset and package authority

**Status:** OPEN-IMPLEMENTATION

### Temuan

Exact duplicate/reuse decisions can identify a primary asset as reusable while component/package authority differs or is incomplete.

### Root cause / failure path

Duplicate identity is evaluated too narrowly if package membership is not part of the canonical identity for package-backed media.

### Penyelesaian

For package-backed media, duplicate/reuse authority must include the required component set and relevant hashes/provenance. Reuse must resolve one complete authoritative asset, not a hybrid of old primary bytes and newly imported components.

### Penanggulangan

Test identical primary with different component; identical package; missing component; reused asset with degraded package health.

### Verification

No duplicate decision may produce split physical authority.

---

## X16 — Missing-dependency acknowledgement is not an executable durable decision

**Status:** OPEN-IMPLEMENTATION

### Temuan

The system can surface missing dependency information but the user acknowledgement/decision path is not guaranteed to produce a durable state that admission, verification, restart, and finalization all understand.

### Root cause / failure path

UI acknowledgement and domain admission state are separate concepts.

### Penyelesaian

Define a durable explicit decision contract such as:

- reject item;
- accept degraded import for supported cases;
- supply/resolve dependency;
- defer.

Persist the choice with row-version semantics and consume it in readiness/verification.

### Penanggulangan

Restart after acknowledgement must reproduce the same decision. Unsupported degraded cases must remain blocked.

### Verification

Missing-dependency workflow from detection through restart and publish.

---

## X17 — Some placement paths bypass the collision-safe allocator

**Status:** OPEN-IMPLEMENTATION

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

---

## X18 — Manifest-derived path containment is too weak if based on string prefix

**Status:** OPEN-IMPLEMENTATION

### Temuan

Any manifest/recovery path validation that relies on textual prefix comparison can be bypassed by normalization, sibling-prefix, traversal, separator, or reparse behavior.

### Root cause / failure path

Path text is treated as authority instead of the canonical filesystem location.

### Penyelesaian

All manifest-derived or recovery-derived paths must pass the shared RootPathRules-style contract:

- full qualification;
- GetFullPath normalization;
- same-root/within-root semantics using separator boundaries;
- traversal rejection;
- reparse-point policy;
- expected operation-scoped root.

### Penanggulangan

Sibling-prefix, dot-dot, alternate separator, case, Unicode, junction/symlink/reparse, root path, and drive-relative tests.

### Verification

Negative containment suite over every manifest/recovery consumer.

---

# 9. Stage 5 — Jobs / Scheduler / Concurrency / Recovery Findings

## X19 — Pause intent can become durable CANCELLED

**Status:** OPEN-IMPLEMENTATION

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

---

## X20 — ImportFinalizer is outside Pause/Cancel quiescence ownership

**Status:** OPEN-IMPLEMENTATION

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

---

## X21 — Lifecycle/checkpoint writes are not sufficiently monotonic against stale writers

**Status:** OPEN-IMPLEMENTATION

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

---

## X22 — Shutdown timeout can release VaultLock while mutation disposal is still active

**Status:** OPEN-IMPLEMENTATION

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

---

## X23 — Session marker is not yet a complete crash protocol

**Status:** OPEN-IMPLEMENTATION

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

---

## X24 — Start/Prioritize can act on stale ImportUnit lifecycle

**Status:** OPEN-IMPLEMENTATION

### Temuan

A control command can resolve an ImportUnit/job target, then act after the unit has transitioned to a state where priority/start semantics no longer apply.

### Root cause / failure path

Control selection and control mutation are not one row-versioned decision.

### Penyelesaian

Control command must:

- read state/version;
- validate allowed lifecycle;
- perform priority/start mutation under the same expected version or transactional predicate;
- no-op/reject stale targets deterministically.

### Penanggulangan

Race Publish/Cancel/Fail/Complete against Start/Prioritize.

### Verification

No terminal unit becomes runnable due to stale control.

---

## X25 — Optional Stage 2 capabilities can incorrectly gate readiness or terminal failure

**Status:** OPEN-IMPLEMENTATION

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

---

## X26 — Forced shutdown lacks semantic Shutdown intent and can persist resumable work as CANCELLED

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

---

## X31 — ExpectedSha256 is carried to the worker but not proven against analyzed bytes

**Status:** OPEN-IMPLEMENTATION

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

---

## X32 — IdentityBankProvider cache can remain stale after face confirmation changes samples

**Status:** OPEN-IMPLEMENTATION

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

---

## X33 — SFace embedding is persisted with YuNet model provenance

**Status:** OPEN-IMPLEMENTATION

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

---

## X34 — Worker crash breaker can restart indefinitely across successful handshakes

**Status:** OPEN-IMPLEMENTATION

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

---

## X35 — Identity bank can exceed the 4 MiB protocol frame limit

**Status:** OPEN-IMPLEMENTATION

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

---

## X36 — ReleaseIndex is only guaranteed on the matching happy path

**Status:** OPEN-IMPLEMENTATION

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

---

# 12. Stage 7 — Profiles / Relations / Trash / Purge

## X37 — Profile Purge FK closure omits import_assignment_clusters references

**Status:** OPEN-IMPLEMENTATION

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

---

# 13. Stage 8 — Application / UI Integration Findings

## X38 — Profile hydration does not await UI commit

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

## X44 — Release/package validation does not share one complete required-member contract

**Status:** OPEN-IMPLEMENTATION

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

## X45 — Update trust lacks independent publisher authenticity

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

## X50 — Canonical toolchain/dependency graph is not reproducibly locked

**Status:** OPEN-IMPLEMENTATION

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

# 15. Stage 10 — Cross-Domain Adversarial Findings

## X51 — Final-attempt crash can leave a job RUNNABLE but permanently unclaimable

**Status:** OPEN-IMPLEMENTATION

### Temuan

Interrupted RUNNING jobs can be blanket-reconciled to PENDING. Retry-exhaustion logic can then project the job RUNNABLE even when attempt >= max attempts, while claim logic refuses to claim it.

### Root cause / failure path

Restart reconciliation, retry-budget classification, and scheduler claim eligibility use different transition authorities.

### Penyelesaian

Create one atomic interrupted-job reconciliation decision using:

- prior durable outcome/checkpoint;
- current attempt;
- max attempts;
- retry classification;
- control intent.

A retry-exhausted job must become terminal, never PENDING/RUNNABLE.

### Penanggulangan

Crash every attempt including the final attempt. Invariant:

attempt >= max and not explicitly reset ⇒ state is not PENDING/RUNNABLE.

### Verification

Zero permanently unclaimable runnable jobs.

---

## X52 — Partial startup rollback can release VaultLock before mutation-capable runtime stops

**Status:** OPEN-IMPLEMENTATION

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

---

## X53 — NeedsAttention conflates advisory issues with writable-startup authority ambiguity

**Status:** OPEN-IMPLEMENTATION

### Temuan

Recovery findings under NeedsAttention can include harmless/retryable maintenance and serious authority ambiguity such as commit conflict, ambiguous recovery, profile rename ambiguity, owner relocation ambiguity, unreadable Trash, pending cancellation, or orphan staging.

If startup only blocks Fatal findings, the writable shell can open while authority is unresolved.

### Root cause / failure path

Severity/presentation category is used as safety authority.

### Penyelesaian

Add an explicit safety classification:

- Advisory;
- RetryableMaintenance;
- BlocksAffectedCapability;
- BlocksWritableStartup;
- Fatal.

Or add a direct BlocksWritableStartup contract to every recovery finding.

Startup uses aggregate safety, not the label NeedsAttention.

### Penanggulangan

Maintain a table: recovery code → safety class → writable allowed yes/no. Test every recovery code.

### Verification

All authority ambiguity reaches a non-writable recovery surface.

---

## X54 — Global mutation command admission remains open while shutdown is already awaiting

**Status:** OPEN-IMPLEMENTATION

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

---

## X55 — Application shutdown deadline and updater parent-wait contract are not one end-to-end deadline

**Status:** OPEN-IMPLEMENTATION

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

---

# 16. Stage 11 — Build / Package / Runtime Verification Findings

These findings are evidence defects. They are not satisfied by source inspection.

## X56 — Exact-tree canonical build evidence is absent

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** CLOSED-DOC

### Temuan

History references a prior range reaching X29, but exact reliable records for X27–X29 cannot be recovered.

### Penyelesaian

Reserve X27–X29 permanently. Do not fabricate contents and do not renumber later findings.

### Penanggulangan

Future finding register records ID, stage, title, evidence, root cause, solution, prevention, verification, status, and provenance atomically.

### Verification

The canonical ledger must contain no X27/X28/X29 finding body, must mark all three IDs RESERVED, and must preserve every later ID unchanged.

---

## X63 — Historical finding-ID collision in an old Stage 7 response

**Status:** CLOSED-DOC

### Temuan

An old Stage 7 response reused X06–X09 for Profile/Trash findings, colliding with established Stage 3 and Stage 4 IDs.

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

**Status:** OPEN-IMPLEMENTATION

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

## X65 — Canonical release ZIP directory entries are rejected by the updater

**Status:** OPEN-IMPLEMENTATION

### Temuan

scripts/package-win-x64.ps1 creates the release ZIP using Compress-Archive over InstallRoot. The package contains directory structure such as workers/, tools/, LICENSES/, and other nested directories. UpdatePackageStager currently rejects every archive entry whose normalized name ends with /.

Relevant paths:

- scripts/package-win-x64.ps1
- src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs

The updater can therefore reject a ZIP produced by the canonical packager.

### Root cause / failure path

Packager models directory entries as legitimate ZIP structure. Stager models every ZIP entry as a file and treats a trailing slash as unsafe.

canonical package → valid directory entry → normalized.EndsWith('/') → rejection.

### Penyelesaian

Teach the stager to distinguish safe directory entries from file entries.

For a directory entry:

- validate the canonical contained path exactly as strictly as a file;
- require directory-entry semantics (no file payload authority);
- create/recognize the directory safely;
- do not add the directory to file-membership/hash validation;
- reject file/directory canonical-path collisions;
- reject unsafe/traversing/rooted/reparse escapes.

For a file entry, retain all current size, compression-ratio, duplicate-name, containment, and CreateNew rules.

### Penanggulangan

The exact ZIP emitted by package-win-x64.ps1 must be fed into UpdatePackageStager during release verification. Include nested empty/non-empty directories and malicious directory names.

### Verification

Canonical ZIP → stager → manifest validation must PASS without special repacking, while unsafe directory entries remain rejected.

---

## X66 — Cancellation terminal projection is delayed until recovery polling on cancellation paths

**Status:** OPEN-IMPLEMENTATION

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

---

## X67 — Single-click Media Detail is intentionally delayed by the Windows double-click timeout

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

---

## X70 — Clipboard retry blocks the UI thread for up to approximately 200 ms

**Status:** OPEN-IMPLEMENTATION

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

**Status:** OPEN-IMPLEMENTATION

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

## X72 — REUSE authority is not revalidated at the commit boundary

**Status:** OPEN-IMPLEMENTATION

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

---

## X73 — Cancelling one import can trash an asset currently reused by another import

**Status:** OPEN-IMPLEMENTATION

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
| Runtime/Import | 38 | Stages 1, 4, 5, 10, 11, 12; X09–X26, X25, X69, X72–X73 |
| Runtime/Localization | 4 | Stages 1, 8, 10, 11; X40 |
| Runtime/Maintenance | 10 | Stages 1, 3, 4, 7, 10, 11; X13 and lifecycle integrity coverage |
| Runtime/Media | 24 | Stages 1, 4, 8, 10, 11, 12; X09–X18, X42, X67 |
| Runtime root project/global usings | 2 | Stages 1, 9, 11; build/toolchain coverage |
| Runtime/Presentation | 12 | Stages 1, 8, 10, 11; presentation install/runtime traced; no unique additional finding |
| Runtime/Profiles | 10 | Stages 1, 2, 3, 7, 8, 10, 11; X01, X04–X08, X37–X42 |
| Runtime/RelatedProfiles | 5 | Stages 1, 6, 7, 8, 10, 11; related-evidence lifecycle traced |
| Runtime/Settings | 4 | Stages 1, 8, 10, 11; X39–X41 |
| Runtime/Shell | 15 | Stages 1, 8, 10, 11, 12; X38–X42, X53–X55, X68 |
| SystemServices/Cache | 15 | Stages 1, 4, 6, 8, 10, 11; cache/resource lifetime traced |
| SystemServices/Database | 49 | Stages 1, 3–7, 10, 11; X06–X08, X19–X26, X37, X51, X69, X72–X73 |
| SystemServices/Diagnostics | 2 | Stages 1, 10, 11; runtime evidence support |
| SystemServices/Jobs | 27 | Stages 1, 5, 6, 10, 11, 12; X19–X26, X30–X36, X51, X66, X69 |
| SystemServices/Lifecycle | 9 | Stages 1, 2, 5, 10, 11, 12; X01, X22–X23, X52–X55, X68 |
| SystemServices/MediaTools | 4 | Stages 1, 4, 9, 11, 12; X44, X57, X71 |
| SystemServices/Operations | 6 | Stages 1, 3–5, 7, 10, 11; idempotency/operation authority coverage |
| SystemServices/ProductIdentity.cs | 1 | Stages 1, 9, 11; X49–X50 |
| SystemServices/Recovery | 8 | Stages 1, 3–5, 7, 9–11; X08, X23, X47–X48, X51–X55 |
| SystemServices/Resources | 2 | Stages 1, 5, 8, 10, 11; resource-governor/lifetime coverage |
| SystemServices/Storage | 23 | Stages 1, 4, 7, 9–11; X04–X18, X37, X52 |
| SystemServices/TimeAndIds | 2 | Stages 1, 3, 5, 10; identity/time durability support |
| SystemServices/UiPrimitives.cs | 1 | Stages 1, 8, 10; UI primitive coverage |
| SystemServices/Updates | 14 | Stages 1, 9–12; X02, X18, X43–X50, X55, X60, X64–X65 |
| SystemServices/Win32Clipboard.cs | 1 | Stages 1, 8, 12; X70 |
| Runtime/Trash | 9 | Stages 1, 3, 7, 10, 11; X06–X08, X37 |
| Neuterradise.Updater | 3 | Stages 1, 9–11; X02, X45–X49, X55, X60 |

**Total: 388 / 388 baseline files owned by the audit map.**

## 20.1 Cross-cutting failure-mode coverage

Coverage is not based only on directory ownership. The audit explicitly traced these failure dimensions end-to-end:

| Failure dimension | Owning findings/stages |
| --- | --- |
| DB trigger/FK invariant conflict | X06–X08, X37; Stages 3 and 7 |
| crash between physical I/O and DB checkpoint | X08–X12, X37, X47, X51–X55 |
| cancellation/pause/shutdown intent races | X19–X26, X51–X55, X66, X69, X73, X66, X69, X73 |
| stale writer / CAS / terminal-state escape | X21, X24, X51 |
| shared asset / cross-import lifetime | X15, X69, X72–X73 |
| path traversal / containment / reparse | X18, X64–X65 |
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

X01, X07, X08, X37, X38, X42

Shared contract:

- valid profile graph is defined once;
- lifecycle transitions close identity/relation/appearance authority;
- UI projection never outruns durable state;
- restart sees the same valid graph.

## Cluster B — Managed path / physical authority

X04, X05, X09–X18

Shared contract:

- path intent is durable before physical mutation;
- collision authority is canonical;
- component/package membership is explicit;
- destructive cleanup is object-safe;
- crash replay is idempotent.

## Cluster C — Scheduler / command / shutdown

X19–X26, X51–X55

Shared contract:

- explicit control intent;
- monotonic terminal state;
- one mutation lease;
- admission closes before quiescence;
- writers stop before VaultLock release;
- interrupted resumable work remains reclaimable.

## Cluster D — Faces / Worker

X03, X30–X36, X59

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

X02, X18, X43–X50, X55, X57, X60, X64–X65, X71

Shared contract:

signed/pinned authority → package completeness → compatibility → staged content revalidation → replacement → recovery → verified terminal cleanup.

---

# 22. Direct remediation order

The following order minimizes rework and prevents fixing symptoms before their authority layer.

## Phase R0 — Freeze audit authority

- Commit and retain this file.
- Do not renumber the ledger.
- Do not open Stage 13 for this baseline.
- New source defects discovered during implementation use X64+ only if they are genuinely outside the existing finding root cause.

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
- X65;
- X71.

Reason: later fixes depend on stable appearance, deployment, model, path-job, package, compatibility, and build authorities.

Do not start broad runtime acceptance until these authorities are coherent.

## Phase R2 — Fix storage/import durability

Target:

- X09–X18;
- X69;
- X72;
- X73.

Implement as a coordinated storage/import pass, not ten isolated local patches.

Acceptance:

- exact replay authority;
- source deletion fail-closed;
- package/component authority durable;
- cancellation/compensation complete;
- health/dependency/duplicate semantics package-aware;
- collision and containment canonical.

## Phase R3 — Fix Trash/Profile lifecycle

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

Target:

- X19–X26;
- X51–X55;
- X66;
- X69;
- X73.

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

Target:

- X30–X36.

Do this after X03 because Worker model topology is an upstream authority.

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

Target:

- remaining X45–X48 plus integration with X02/X43–X50/X55/X64–X65/X71.

Acceptance:

- independent publisher authenticity;
- least-privilege workflow;
- typed recovery;
- terminal cleanup;
- full manifest trust chain.

## Phase R8 — Build and executable verification

Target:

- X56–X61.

This phase is mandatory after source remediation.

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

1. Read the finding and every finding in its dependency cluster.
2. Trace all current callers before editing.
3. Change the canonical authority rather than adding a second authority.
4. Preserve existing safety constraints unless the finding explicitly proves they are contradictory.
5. Add the regression guard in the same implementation unit.
6. Verify failure/recovery paths, not only happy path.
7. Record the commit/SHA that closes the finding.
8. Do not mark SOURCE-CLOSED when only a symptom is patched.
9. Do not delete recovery evidence merely to make a test pass.
10. Do not disable triggers/FKs/integrity checks to make lifecycle operations succeed.
11. Do not add alternate build/package/update paths.
12. Do not touch the real Vault during build/package/release verification.

Repository operating rules from AGENTS.md remain authoritative for canonical build/release commands and cache/Vault handling.

---

# 24. Finding closure record format

When implementation begins, each finding entry should append a closure record using this exact structure:

**Implementation SHA:** exact commit  
**Changed paths:** exhaustive intended paths  
**Root-cause correction:** what authority/state machine was changed  
**Regression guard:** exact automated guard  
**Verification result:** command/harness and result  
**Dependency findings checked:** IDs  
**Source status:** SOURCE-CLOSED or still OPEN-IMPLEMENTATION  
**Runtime status:** VERIFIED/RUNTIME-VERIFIED only when actually executed  
**Residual risk/blocker:** none or exact blocker

Never replace the original Temuan/Penyelesaian/Penanggulangan text with only the closure record. The reason for the fix must remain visible.

---

# 25. Final acceptance criteria

NeuTerradise may be described as remediated against this Deep Audit only when all of the following are true:

1. All remediation/evidence findings X01–X61 and X64–X73 are SOURCE-CLOSED or explicitly superseded by a documented X74+ finding with evidence.
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

The correct next action is implementation against this document, not another full audit of the same unchanged source tree.

If implementation reveals a genuinely distinct defect outside the root causes of X01–X73, allocate X74 and continue monotonically. If an observation is another manifestation of an existing root cause, attach it to that existing finding instead of creating a duplicate.

**Canonical conclusion: Deep Audit Stage 1–12 CLOSED-DOC with final coverage certification; remediation and executable verification remain open. Full source re-audit of the unchanged frozen baseline is not required.**
