using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Import;

namespace Neuterradise.App.Maintenance;

public sealed class RepairExecutor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _vaultExecutionGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CatalogDb _catalog;
    private readonly ProfileManifestWriter _manifestWriter;
    private readonly SourceCleanupExecutor _sourceCleanupExecutor;
    private readonly OperationExecution _execution;

    /// <summary>Canonical operation kind of an executed repair plan (Section 7.1).</summary>
    public const string ExecuteRepairKind = "MAINTENANCE_EXECUTE_REPAIR";

    public RepairExecutor(
        CatalogDb catalog,
        ProfileManifestWriter? manifestWriter = null,
        SourceCleanupExecutor? sourceCleanupExecutor = null,
        StructuredDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _manifestWriter = manifestWriter ?? new ProfileManifestWriter(catalog.Paths);
        _sourceCleanupExecutor = sourceCleanupExecutor ?? new SourceCleanupExecutor(
            catalog.Paths,
            new ManagedFileVerifier(),
            catalog.ImportWrites);
        _execution = new OperationExecution(diagnostics);
    }

    /// <summary>
    /// Executes one prepared repair plan. The plan already carries the logical OperationId, so a
    /// retry resumes the same operation instead of starting a second one, and cancellation or an
    /// infrastructure fault returns a canonical result instead of an exception to the surface.
    /// </summary>
    public Task<OperationResult<RepairPlan>> ExecuteAsync(
        RepairPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var context = OperationContext.Resume(
            ExecuteRepairKind,
            plan.OperationId,
            plan.PreparedAtUtc,
            cancellationToken) with
        {
            ProfileId = plan.ProfileId,
            AssetId = plan.AssetId,
            ImportItemId = plan.ImportItemId,
        };

        return _execution.RunAsync<RepairPlan>(context, ExecuteGatedAsync);

        async Task<OperationResult<RepairPlan>> ExecuteGatedAsync(OperationContext operation)
        {
            var vaultKey = Path.GetFullPath(_catalog.Paths.Root);
            var executionGate = _vaultExecutionGates.GetOrAdd(vaultKey, static _ => new SemaphoreSlim(1, 1));
            await executionGate.WaitAsync(operation.CancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteCoreAsync(plan, operation.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                executionGate.Release();
            }
        }
    }

    private async Task<OperationResult<RepairPlan>> ExecuteCoreAsync(
        RepairPlan plan,
        CancellationToken cancellationToken)
    {
        var persisted = await _catalog.MaintenanceWrites
            .ReadRepairOperationAsync(plan.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null)
        {
            return OperationResult<RepairPlan>.NotFound(
                OperationErrorCode.RepairPlanNotFound,
                "The persisted Library Repair operation was not found.");
        }

        if (persisted.Plan != plan)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The supplied repair plan does not match its persisted operation.");
        }

        if (string.Equals(persisted.State, "COMPLETED", StringComparison.Ordinal))
        {
            return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
        }

        if (string.Equals(persisted.State, "STALE", StringComparison.Ordinal))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The repair plan was already invalidated by newer authority or physical facts.");
        }

        var checkpointStarted = persisted.State is "EXECUTING" or "FAILED_RETRYABLE";
        var readiness = await RevalidateAsync(plan, checkpointStarted, cancellationToken).ConfigureAwait(false);
        if (readiness == RepairReadiness.Stale)
        {
            await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
                plan.OperationId,
                "STALE",
                OperationErrorCode.RepairPlanStale,
                "Authority or physical facts changed after repair preparation.",
                cancellationToken).ConfigureAwait(false);
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "Authority or physical facts changed after repair preparation.");
        }

        if (!string.Equals(persisted.State, "EXECUTING", StringComparison.Ordinal))
        {
            await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
                plan.OperationId,
                "EXECUTING",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        StorageOperationResult actionResult;
        if (readiness == RepairReadiness.AlreadyApplied)
        {
            actionResult = new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }
        else
        {
            actionResult = plan.Kind switch
            {
                RepairKind.RegenerateProfileManifest when plan.ProfileId is Guid profileId =>
                    await _manifestWriter.RegenerateManifestAsync(
                        _catalog,
                        profileId,
                        plan.OperationId,
                        cancellationToken).ConfigureAwait(false),
                RepairKind.RetryCommittedSourceDelete when plan.ImportItemId is Guid importItemId =>
                    await _sourceCleanupExecutor.ExecuteAsync(importItemId, cancellationToken).ConfigureAwait(false),
                _ => new StorageOperationResult(
                    StorageOperationStatus.NeedsAttention,
                    SafeErrorDetail: "The persisted repair kind has no executor."),
            };
        }

        if (!actionResult.IsSuccess)
        {
            await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
                plan.OperationId,
                "FAILED_RETRYABLE",
                OperationErrorCode.RepairExecutionFailed,
                SafeFailureDetail(actionResult),
                CancellationToken.None).ConfigureAwait(false);
            return OperationResult<RepairPlan>.NeedsAttention(
                OperationErrorCode.RepairExecutionFailed,
                "The repair did not complete and remains safe to retry.",
                plan.OperationId);
        }

        await AppendCompletionActivityAsync(plan).ConfigureAwait(false);
        await _catalog.MaintenanceWrites.SetRepairOperationStateAsync(
            plan.OperationId,
            "COMPLETED",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private async Task<RepairReadiness> RevalidateAsync(
        RepairPlan plan,
        bool checkpointStarted,
        CancellationToken cancellationToken) => plan.Kind switch
        {
            RepairKind.RegenerateProfileManifest =>
                await RevalidateManifestAsync(plan, checkpointStarted, cancellationToken).ConfigureAwait(false),
            RepairKind.RetryCommittedSourceDelete =>
                await RevalidateSourceCleanupAsync(plan, checkpointStarted, cancellationToken).ConfigureAwait(false),
            _ => RepairReadiness.Stale,
        };

    private async Task<RepairReadiness> RevalidateManifestAsync(
        RepairPlan plan,
        bool checkpointStarted,
        CancellationToken cancellationToken)
    {
        if (plan.ProfileId is not Guid profileId)
        {
            return RepairReadiness.Stale;
        }

        var healthProfile = (await _catalog.HealthReads.GetHealthProfileItemsAsync(profileId, cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault();
        if (healthProfile is null
            || healthProfile.RowVersion != plan.ExpectedRowVersion
            || healthProfile.IsTrashed
            || healthProfile.PathState != ManagedPathState.None
            || !string.Equals(
                $"{DbEnum.Format(AssetState.Active)}|{DbEnum.Format(ManagedPathState.None)}|{healthProfile.StorageToken}",
                plan.ExpectedAuthorityState,
                StringComparison.Ordinal))
        {
            return RepairReadiness.Stale;
        }

        string manifestPath;
        try
        {
            manifestPath = _catalog.Paths.ResolveVaultRelativePath(plan.TargetPathOrRelative);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return RepairReadiness.Stale;
        }

        var folder = Path.GetDirectoryName(manifestPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return RepairReadiness.Stale;
        }

        if (checkpointStarted && await ManifestMatchesAuthorityAsync(profileId, folder, cancellationToken).ConfigureAwait(false))
        {
            return RepairReadiness.AlreadyApplied;
        }

        return await PhysicalFactMatchesAsync(plan, manifestPath, cancellationToken).ConfigureAwait(false)
            ? RepairReadiness.Ready
            : RepairReadiness.Stale;
    }

    private async Task<bool> ManifestMatchesAuthorityAsync(
        Guid profileId,
        string folder,
        CancellationToken cancellationToken)
    {
        var detail = await _catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken).ConfigureAwait(false);
        var inspection = ProfileManifestWriter.InspectManifest(folder);
        if (detail is null || inspection.Status != ManifestStatus.Valid || inspection.Manifest is null)
        {
            return false;
        }

        var expected = ProfileManifestWriter.CreateManifest(detail, Path.GetFileName(folder));
        return inspection.Manifest == expected;
    }

    private async Task<RepairReadiness> RevalidateSourceCleanupAsync(
        RepairPlan plan,
        bool checkpointStarted,
        CancellationToken cancellationToken)
    {
        if (plan.ImportItemId is not Guid importItemId || plan.AssetId is not Guid assetId)
        {
            return RepairReadiness.Stale;
        }

        var obligation = await _catalog.ImportWrites.ReadSourceCleanupObligationAsync(
            importItemId,
            cancellationToken).ConfigureAwait(false);
        if (obligation is null
            || obligation.CandidateAssetId != assetId
            || !SourceCleanupExecutor.HasStableCommittedAuthority(obligation)
            || !string.Equals(
                RepairSourceAuthoritySnapshot.Capture(obligation),
                plan.ExpectedAuthorityState,
                StringComparison.Ordinal)
            || !string.Equals(obligation.SourcePath, plan.SourcePathOrRelative, StringComparison.Ordinal)
            || obligation.CandidateSha256 is null
            || obligation.CandidateByteLength is null
            || !string.Equals(obligation.CandidateSha256, plan.ExpectedPhysicalSha256, StringComparison.OrdinalIgnoreCase)
            || obligation.CandidateByteLength != plan.ExpectedPhysicalByteLength
            || obligation.CurrentManagedRelativePath is null
            || obligation.CurrentManagedFileName is null
            || !string.Equals(
                CombineRelative(obligation.CurrentManagedRelativePath, obligation.CurrentManagedFileName),
                plan.TargetPathOrRelative,
                StringComparison.OrdinalIgnoreCase))
        {
            return RepairReadiness.Stale;
        }

        if (checkpointStarted
            && obligation.SourceCleanupState == SourceCleanupState.SourceConsumed
            && !File.Exists(obligation.SourcePath))
        {
            return RepairReadiness.AlreadyApplied;
        }

        if (obligation.SourceCleanupState is not (SourceCleanupState.SourceDeletePending
                or SourceCleanupState.SourceDeleteFailed
                or SourceCleanupState.SourceChanged))
        {
            return RepairReadiness.Stale;
        }

        if (!checkpointStarted)
        {
            var expectedCleanupState = plan.FindingCode switch
            {
                HealthFindingCode.SourceDeletePending => SourceCleanupState.SourceDeletePending,
                HealthFindingCode.SourceDeleteFailed => SourceCleanupState.SourceDeleteFailed,
                _ => SourceCleanupState.SourceChanged,
            };
            var items = await _catalog.HealthReads.GetSourceCleanupRepairItemsAsync(importItemId, cancellationToken)
                .ConfigureAwait(false);
            if (items.Count != 1
                || items[0].RowVersion != plan.ExpectedRowVersion
                || items[0].SourceCleanupState != expectedCleanupState)
            {
                return RepairReadiness.Stale;
            }
        }

        if (!File.Exists(obligation.SourcePath))
        {
            return checkpointStarted ? RepairReadiness.Ready : RepairReadiness.Stale;
        }

        return await PhysicalFactMatchesAsync(plan, obligation.SourcePath, cancellationToken).ConfigureAwait(false)
            ? RepairReadiness.Ready
            : RepairReadiness.Stale;
    }

    private static async Task<bool> PhysicalFactMatchesAsync(
        RepairPlan plan,
        string path,
        CancellationToken cancellationToken)
    {
        if (plan.CurrentPhysicalFact == RepairPhysicalFact.ManifestMissing)
        {
            return !File.Exists(path);
        }

        if (!File.Exists(path)
            || plan.ExpectedPhysicalSha256 is null
            || plan.ExpectedPhysicalByteLength is null)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        if (stream.Length != plan.ExpectedPhysicalByteLength.Value)
        {
            return false;
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexStringLower(hash),
            plan.ExpectedPhysicalSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task AppendCompletionActivityAsync(RepairPlan plan)
    {
        var payload = JsonSerializer.Serialize(new
        {
            repairPlanId = plan.RepairPlanId,
            repairKind = plan.Kind.ToString(),
            findingCode = plan.FindingCode,
        });
        await _catalog.ActivityWrites.AppendAsync(new ActivityEntryPersistence(
            plan.RepairPlanId,
            ActivityEventType.LibraryRepairCompleted,
            plan.ProfileId,
            plan.AssetId,
            null,
            plan.OperationId,
            payload,
            plan.PreparedAtUtc), CancellationToken.None).ConfigureAwait(false);
    }

    private static string SafeFailureDetail(StorageOperationResult result) =>
        string.IsNullOrWhiteSpace(result.SafeErrorDetail)
            ? $"Storage operation reported {result.Status}."
            : result.SafeErrorDetail;

    private static string CombineRelative(string directory, string fileName) =>
        $"{directory.Replace('\\', '/').TrimEnd('/')}/{fileName}";

    private enum RepairReadiness
    {
        Ready,
        AlreadyApplied,
        Stale
    }
}
