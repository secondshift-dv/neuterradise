using System.IO;
using System.Security.Cryptography;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.TimeAndIds;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Import;

namespace Neuterradise.App.Maintenance;

public sealed class RepairPlanner
{
    private readonly CatalogDb _catalog;
    private readonly ManagedPathPlanner _pathPlanner;
    private readonly TimeProvider _timeProvider;
    private readonly OperationExecution _execution;

    /// <summary>Canonical operation kind of preparing a repair plan (Section 7.1).</summary>
    public const string PrepareRepairKind = "MAINTENANCE_PREPARE_REPAIR";

    public RepairPlanner(
        CatalogDb catalog,
        ManagedPathPlanner? pathPlanner = null,
        TimeProvider? timeProvider = null,
        StructuredDiagnostics? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _pathPlanner = pathPlanner ?? new ManagedPathPlanner(_catalog.Paths.Root);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _execution = new OperationExecution(diagnostics, _timeProvider.AsClock());
    }

    /// <summary>
    /// Prepares a repair plan for one finding. Cancellation and infrastructure faults are returned
    /// as canonical results so the Library Health surface never has to interpret an exception.
    /// </summary>
    public Task<OperationResult<RepairPlan>> PrepareAsync(
        HealthFinding finding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return _execution.RunAsync<RepairPlan>(
            OperationContext.Start(PrepareRepairKind, _timeProvider, cancellationToken)
                with { ProfileId = finding.ProfileId, AssetId = finding.AssetId },
            context => PrepareCoreAsync(finding, context.CancellationToken));
    }

    private async Task<OperationResult<RepairPlan>> PrepareCoreAsync(
        HealthFinding finding,
        CancellationToken cancellationToken)
    {

        OperationResult<RepairPlan> prepared;
        if (finding.Code is HealthFindingCode.ProfileManifestMissing
            or HealthFindingCode.ProfileManifestMalformed
            or HealthFindingCode.ProfileManifestStale)
        {
            prepared = await PrepareManifestRepairAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else if (finding.Code is HealthFindingCode.SourceDeletePending
                 or HealthFindingCode.SourceDeleteFailed
                 or HealthFindingCode.SourceChanged)
        {
            prepared = await PrepareSourceCleanupRepairAsync(finding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return Unsupported(finding.Code);
        }

        if (!prepared.IsSuccess || prepared.Value is null)
        {
            return prepared;
        }

        if (!finding.RepairAvailable)
        {
            return Unsupported(finding.Code);
        }

        try
        {
            var persisted = await _catalog.MaintenanceWrites
                .BeginOrReadRepairOperationAsync(prepared.Value, cancellationToken)
                .ConfigureAwait(false);
            return OperationResult<RepairPlan>.Success(persisted, persisted.OperationId);
        }
        catch (CatalogInvariantException)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "A different repair operation already owns this subject.");
        }
    }

    private async Task<OperationResult<RepairPlan>> PrepareManifestRepairAsync(
        HealthFinding finding,
        CancellationToken cancellationToken)
    {
        if (finding.ProfileId is not Guid profileId || profileId == Guid.Empty)
        {
            return OperationResult<RepairPlan>.Validation(
                OperationErrorCode.RepairNotSupported,
                "Manifest repair requires a stable ProfileId.");
        }

        var profile = (await _catalog.HealthReads.GetHealthProfileItemsAsync(profileId, cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault();
        if (profile is null || profile.IsTrashed || string.IsNullOrWhiteSpace(profile.StorageToken)
            || profile.PathState != ManagedPathState.None)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The Profile is not in a stable active state for manifest regeneration.");
        }

        var canonicalFolder = _pathPlanner.PlanProfile(
            profile.ProfileId,
            profile.DisplayName,
            new ProfileStorageToken(profile.StorageToken)).ProfileFolderRelativePath;
        var currentFolder = string.IsNullOrWhiteSpace(profile.CurrentManagedRelativePath)
            ? canonicalFolder
            : NormalizeRelative(profile.CurrentManagedRelativePath);
        if (!string.Equals(currentFolder, canonicalFolder, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "Profile placement must be reconciled before its manifest is regenerated.");
        }

        string absoluteFolder;
        try
        {
            absoluteFolder = _catalog.Paths.ResolveVaultRelativePath(currentFolder);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return OperationResult<RepairPlan>.Validation(
                OperationErrorCode.RepairNotSupported,
                "The Profile folder is outside the managed Profiles area.");
        }

        if (!Directory.Exists(absoluteFolder))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The Profile folder no longer exists at the authoritative path.");
        }

        var inspection = ProfileManifestWriter.InspectManifest(absoluteFolder);
        var expectedFact = finding.Code switch
        {
            HealthFindingCode.ProfileManifestMissing => RepairPhysicalFact.ManifestMissing,
            HealthFindingCode.ProfileManifestMalformed => RepairPhysicalFact.ManifestMalformed,
            _ => RepairPhysicalFact.ManifestStale,
        };
        if (!MatchesManifestFact(
                inspection,
                expectedFact,
                profileId,
                profile.Kind,
                profile.DisplayName,
                Path.GetFileName(canonicalFolder)))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The manifest no longer matches the finding that was selected.");
        }

        var manifestRelative = $"{currentFolder.TrimEnd('/')}/{ProfileManifestWriter.ManifestFileName}";
        var manifestPath = Path.Combine(absoluteFolder, ProfileManifestWriter.ManifestFileName);
        var (length, sha256) = File.Exists(manifestPath)
            ? await FingerprintAsync(manifestPath, cancellationToken).ConfigureAwait(false)
            : ((long?)null, null);
        var plan = new RepairPlan(
            RepairPlan.CurrentSchemaVersion,
            Guid.NewGuid(),
            finding.Code,
            RepairKind.RegenerateProfileManifest,
            profileId,
            null,
            null,
            profile.RowVersion,
            $"ACTIVE|NONE|{profile.StorageToken}",
            manifestRelative,
            manifestRelative,
            sha256,
            length,
            expectedFact,
            "Regenerate profile.json from current catalog authority.",
            IsMaterialOrDestructive: true,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow());
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private async Task<OperationResult<RepairPlan>> PrepareSourceCleanupRepairAsync(
        HealthFinding finding,
        CancellationToken cancellationToken)
    {
        if (finding.AssetId is not Guid subjectId || subjectId == Guid.Empty)
        {
            return OperationResult<RepairPlan>.Validation(
                OperationErrorCode.RepairNotSupported,
                "Source-cleanup repair requires a stable AssetId or ImportItemId.");
        }

        var candidates = await _catalog.HealthReads
            .GetSourceCleanupRepairItemsAsync(subjectId, cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count != 1)
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The finding does not resolve to exactly one committed source-cleanup obligation.");
        }

        var item = candidates[0];
        var expectedState = finding.Code switch
        {
            HealthFindingCode.SourceDeletePending => SourceCleanupState.SourceDeletePending,
            HealthFindingCode.SourceDeleteFailed => SourceCleanupState.SourceDeleteFailed,
            _ => SourceCleanupState.SourceChanged,
        };
        var obligation = await _catalog.ImportWrites.ReadSourceCleanupObligationAsync(
            item.ImportItemId,
            cancellationToken).ConfigureAwait(false);
        if (obligation is null
            || !SourceCleanupExecutor.HasStableCommittedAuthority(obligation)
            || obligation.CandidateAssetId != item.AssetId
            || obligation.SourceCleanupState != expectedState
            || !string.Equals(obligation.SourcePath, item.SourcePath, StringComparison.Ordinal)
            || !string.Equals(obligation.CandidateSha256, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
            || obligation.CandidateByteLength != item.ExpectedByteLength
            || !string.Equals(obligation.CurrentManagedRelativePath, item.CurrentManagedRelativePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(obligation.CurrentManagedFileName, item.CurrentManagedFileName, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(item.SourcePath)
            || !File.Exists(item.SourcePath))
        {
            return OperationResult<RepairPlan>.Conflict(
                OperationErrorCode.RepairPlanStale,
                "The source-cleanup facts changed after the finding was produced.");
        }

        var (physicalLength, physicalSha256) = await FingerprintAsync(item.SourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (physicalLength != item.ExpectedByteLength
            || !string.Equals(physicalSha256, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<RepairPlan>.NeedsAttention(
                OperationErrorCode.RepairPlanStale,
                "The external source no longer matches the durable cleanup obligation.");
        }

        var targetRelative = $"{NormalizeRelative(item.CurrentManagedRelativePath).TrimEnd('/')}/{item.CurrentManagedFileName}";
        var plan = new RepairPlan(
            RepairPlan.CurrentSchemaVersion,
            Guid.NewGuid(),
            finding.Code,
            RepairKind.RetryCommittedSourceDelete,
            null,
            item.AssetId,
            item.ImportItemId,
            item.RowVersion,
            RepairSourceAuthoritySnapshot.Capture(obligation),
            item.SourcePath,
            targetRelative,
            physicalSha256,
            physicalLength,
            RepairPhysicalFact.SourcePresent,
            "Retry deletion of the exact verified source for an already-committed Move.",
            IsMaterialOrDestructive: true,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow());
        return OperationResult<RepairPlan>.Success(plan, plan.OperationId);
    }

    private static bool MatchesManifestFact(
        ManifestInspectionResult inspection,
        RepairPhysicalFact expected,
        Guid profileId,
        ProfileKind profileKind,
        string displayName,
        string folderName) => expected switch
        {
            RepairPhysicalFact.ManifestMissing => inspection.Status == ManifestStatus.Missing,
            RepairPhysicalFact.ManifestMalformed => inspection.Status == ManifestStatus.Malformed,
            RepairPhysicalFact.ManifestStale =>
                inspection.Status == ManifestStatus.Valid
                && inspection.Manifest is { } manifest
                && manifest.ProfileId == profileId
                && (!string.Equals(manifest.DisplayName, displayName, StringComparison.Ordinal)
                    || manifest.Kind != profileKind
                    || !string.Equals(manifest.FolderName, folderName, StringComparison.Ordinal)),
            _ => false,
        };

    private static async Task<(long? Length, string? Sha256)> FingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (stream.Length, Convert.ToHexStringLower(hash));
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private static OperationResult<RepairPlan> Unsupported(string code) =>
        OperationResult<RepairPlan>.Validation(
            OperationErrorCode.RepairNotSupported,
            $"{code} has no conservative automatic repair.");
}
