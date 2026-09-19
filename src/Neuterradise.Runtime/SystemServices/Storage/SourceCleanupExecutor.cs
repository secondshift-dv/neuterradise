using System.IO;
using System.Security.Cryptography;
using Neuterradise.App.Import;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class SourceCleanupExecutor
{
    // A source may only be deleted once the library durably owns the media (Section 44.2.3).
    private static bool IsSafeLibraryCommitState(ImportCommitCheckpoint checkpoint) =>
        checkpoint.HasReachedDomainCommit();

    private readonly VaultPaths _paths;
    private readonly ManagedFileVerifier _verifier;
    private readonly ImportWrites _importWrites;

    public SourceCleanupExecutor(
        VaultPaths paths,
        ManagedFileVerifier verifier,
        ImportWrites importWrites)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _importWrites = importWrites ?? throw new ArgumentNullException(nameof(importWrites));
    }

    public async Task<StorageOperationResult> ExecuteAsync(
        Guid importItemId,
        CancellationToken cancellationToken = default)
    {
        if (importItemId == Guid.Empty)
        {
            throw new ArgumentException("A stable ImportItemId cannot be empty.", nameof(importItemId));
        }

        try
        {
            return await ExecuteCoreAsync(importItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageOperationResult(StorageOperationStatus.Cancelled);
        }
    }

    private async Task<StorageOperationResult> ExecuteCoreAsync(
        Guid importItemId,
        CancellationToken cancellationToken)
    {
        var obligation = await _importWrites.ReadSourceCleanupObligationAsync(
                importItemId,
                cancellationToken)
            .ConfigureAwait(false);
        if (obligation is null)
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted source-cleanup obligation does not exist.");
        }

        if (obligation.SourceCleanupState is SourceCleanupState.SourceConsumed or SourceCleanupState.SourcePreserved)
        {
            return new StorageOperationResult(StorageOperationStatus.AlreadyCompleted);
        }

        // Copy policy NEVER authorizes source deletion.
        if (obligation.CleanupPolicy == ImportCleanupPolicy.Copy)
        {
            await _importWrites.MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
                .ConfigureAwait(false);
            return new StorageOperationResult(StorageOperationStatus.Success);
        }

        // MOVE is destructive. Package discovery must be positively complete; parser failure,
        // unsupported discovery, missing dependencies, or unknown discovery all preserve source.
        if (obligation.DependencyStatus is AssetDependencyStatus.DependenciesMissing or AssetDependencyStatus.DependenciesUnknown
            || obligation.DependencyDiscoveryState is not (null or DependencyDiscoveryState.Complete))
        {
            await _importWrites.MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
                .ConfigureAwait(false);
            return new StorageOperationResult(StorageOperationStatus.Success);
        }

        if (obligation.SourceCleanupState is not (SourceCleanupState.SourceDeletePending
                or SourceCleanupState.SourceDeleteFailed)
            || !HasStableCommittedAuthority(obligation))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "Persisted business authority does not permit source cleanup.");
        }

        if (!Path.IsPathFullyQualified(obligation.SourcePath))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The persisted cleanup source path is not fully qualified.");
        }

        // Vault bytes are never disposable import-source material. Even a persisted MOVE obligation
        // must not authorize deletion when its original source resolves inside the current Vault.
        // This guard lives at the final deletion boundary so older/recovered jobs inherit the same rule.
        if (IsWithinVault(obligation.SourcePath))
        {
            await _importWrites.MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
                .ConfigureAwait(false);
            return new StorageOperationResult(
                StorageOperationStatus.Success,
                SafeErrorDetail: "The import source is inside the Vault and was preserved.");
        }

        string managedPath;
        try
        {
            managedPath = _paths.ResolveVaultRelativePath(
                CombineRelative(
                    obligation.CurrentManagedRelativePath!,
                    obligation.CurrentManagedFileName!));
        }
        catch (ArgumentException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }
        catch (IOException)
        {
            return new StorageOperationResult(StorageOperationStatus.PathOutsideVault);
        }

        if (string.Equals(
                Path.GetFullPath(obligation.SourcePath),
                managedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return new StorageOperationResult(
                StorageOperationStatus.NeedsAttention,
                SafeErrorDetail: "The cleanup source resolves to the authoritative managed destination.");
        }

        var managedVerification = await _verifier.VerifyAsync(
                managedPath,
                obligation.ExpectedByteLength!.Value,
                obligation.ExpectedSha256!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!managedVerification.IsMatch)
        {
            return managedVerification.Status switch
            {
                ManagedFileVerificationStatus.Cancelled => new StorageOperationResult(
                    StorageOperationStatus.Cancelled,
                    managedVerification.Status),
                ManagedFileVerificationStatus.LengthMismatch or ManagedFileVerificationStatus.HashMismatch =>
                    new StorageOperationResult(
                        StorageOperationStatus.VerificationFailed,
                        managedVerification.Status),
                _ => new StorageOperationResult(
                    StorageOperationStatus.NeedsAttention,
                    managedVerification.Status,
                    managedVerification.SafeErrorDetail),
            };
        }

        // X11: destructive source authority always comes from the current Candidate.
        // The managed/reused Asset is verification authority only; its historical source metadata
        // is never allowed to nominate an external path for deletion.
        var sourceAssetId = obligation.CandidateAssetId;
        var targetAssetId = obligation.ManagedAssetId;
        var sourceComponents = sourceAssetId is null
            ? Array.Empty<AssetComponentRecord>()
            : await _importWrites.ReadAssetComponentsAsync(sourceAssetId.Value, cancellationToken).ConfigureAwait(false);
        var targetComponents = targetAssetId is null
            ? Array.Empty<AssetComponentRecord>()
            : await _importWrites.ReadAssetComponentsAsync(targetAssetId.Value, cancellationToken).ConfigureAwait(false);

        var isMultiFile = sourceComponents.Count > 1
            || targetComponents.Count > 1
            || sourceComponents.Any(c => c.ComponentRole == ComponentRole.Dependency)
            || targetComponents.Any(c => c.ComponentRole == ComponentRole.Dependency);

        if (isMultiFile)
        {
            if (sourceAssetId is null
                || targetAssetId is null
                || sourceComponents.Count == 0
                || sourceComponents.Count != targetComponents.Count)
            {
                await _importWrites.MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
                    .ConfigureAwait(false);
                return new StorageOperationResult(
                    StorageOperationStatus.NeedsAttention,
                    SafeErrorDetail: "Package component authority is incomplete; external source was preserved.");
            }

            var targetByNormalized = targetComponents.ToDictionary(
                component => component.NormalizedComponentPath,
                StringComparer.Ordinal);
            foreach (var sourceComponent in sourceComponents)
            {
                if (!targetByNormalized.TryGetValue(sourceComponent.NormalizedComponentPath, out var targetComponent)
                    || targetComponent.ByteLength != sourceComponent.ByteLength
                    || !string.Equals(targetComponent.Sha256, sourceComponent.Sha256, StringComparison.Ordinal))
                {
                    await _importWrites.MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new StorageOperationResult(
                        StorageOperationStatus.NeedsAttention,
                        SafeErrorDetail: "Candidate and managed package component authorities do not reconcile.");
                }
            }

            var primarySourceDir = Path.GetDirectoryName(obligation.SourcePath) ?? string.Empty;
            var componentSources = sourceComponents.ToDictionary(
                component => component.NormalizedComponentPath,
                component => !string.IsNullOrWhiteSpace(component.OriginalSourcePath)
                    ? component.OriginalSourcePath
                    : Path.Combine(
                        primarySourceDir,
                        component.ComponentRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.Ordinal);

            if (componentSources.Values.Any(IsWithinVault))
            {
                await _importWrites.MarkSourceCleanupPreservedAsync(obligation, CancellationToken.None)
                    .ConfigureAwait(false);
                return new StorageOperationResult(
                    StorageOperationStatus.Success,
                    managedVerification.Status,
                    "At least one package source component is inside the Vault; all source components were preserved.");
            }

            var hasChanged = false;
            var hasFailed = false;
            foreach (var sourceComponent in sourceComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sourceComponent.SourceCleanupState == SourceCleanupState.SourceConsumed)
                {
                    continue;
                }

                var targetComponent = targetByNormalized[sourceComponent.NormalizedComponentPath];
                var compSourcePath = componentSources[sourceComponent.NormalizedComponentPath];

                string compManagedPath;
                try
                {
                    compManagedPath = _paths.ResolveVaultRelativePath(
                        CombineRelative(
                            obligation.CurrentManagedRelativePath!,
                            targetComponent.ComponentRelativePath));
                }
                catch (Exception exception) when (exception is ArgumentException or IOException)
                {
                    await _importWrites.UpdateComponentCleanupStateAsync(
                        sourceAssetId.Value,
                        sourceComponent.ComponentRelativePath,
                        SourceCleanupState.SourceChanged,
                        "Managed counterpart path is invalid.",
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    hasChanged = true;
                    continue;
                }

                var compManagedVerification = await _verifier.VerifyAsync(
                    compManagedPath,
                    targetComponent.ByteLength,
                    targetComponent.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (!compManagedVerification.IsMatch)
                {
                    await _importWrites.UpdateComponentCleanupStateAsync(
                        sourceAssetId.Value,
                        sourceComponent.ComponentRelativePath,
                        SourceCleanupState.SourceChanged,
                        "Managed counterpart verification failed for this component.",
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    hasChanged = true;
                    continue;
                }

                var (outcome, errorDetail) = await SecureDeleteSourceFileAsync(
                    compSourcePath,
                    sourceComponent.ByteLength,
                    sourceComponent.Sha256,
                    sourceComponent.SourceIdentityJson,
                    cancellationToken).ConfigureAwait(false);

                switch (outcome)
                {
                    case DeleteOutcome.Consumed:
                    case DeleteOutcome.Missing:
                        await _importWrites.UpdateComponentCleanupStateAsync(
                            sourceAssetId.Value,
                            sourceComponent.ComponentRelativePath,
                            SourceCleanupState.SourceConsumed,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        break;
                    case DeleteOutcome.Changed:
                        await _importWrites.UpdateComponentCleanupStateAsync(
                            sourceAssetId.Value,
                            sourceComponent.ComponentRelativePath,
                            SourceCleanupState.SourceChanged,
                            errorDetail,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        hasChanged = true;
                        break;
                    default:
                        await _importWrites.UpdateComponentCleanupStateAsync(
                            sourceAssetId.Value,
                            sourceComponent.ComponentRelativePath,
                            SourceCleanupState.SourceDeleteFailed,
                            errorDetail,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        hasFailed = true;
                        break;
                }
            }

            if (hasChanged)
            {
                await _importWrites.MarkSourceCleanupChangedAsync(
                    obligation,
                    "One or more package components changed or lost managed verification.",
                    CancellationToken.None).ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.SourceChanged);
            }

            if (hasFailed)
            {
                await _importWrites.MarkSourceCleanupFailedAsync(
                    obligation,
                    "One or more package components could not be deleted.",
                    CancellationToken.None).ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.FileLocked);
            }

            await _importWrites.MarkSourceCleanupConsumedAsync(obligation, CancellationToken.None)
                .ConfigureAwait(false);
            return new StorageOperationResult(StorageOperationStatus.Success, managedVerification.Status);
        }

        var (singleOutcome, singleError) = await SecureDeleteSourceFileAsync(
            obligation.SourcePath,
            obligation.CandidateByteLength!.Value,
            obligation.CandidateSha256!,
            obligation.SourceIdentityJson,
            cancellationToken).ConfigureAwait(false);

        switch (singleOutcome)
        {
            case DeleteOutcome.Consumed:
                await _importWrites.MarkSourceCleanupConsumedAsync(obligation, CancellationToken.None)
                    .ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.Success, managedVerification.Status);

            case DeleteOutcome.Missing:
                await _importWrites.MarkSourceCleanupConsumedAsync(obligation, CancellationToken.None)
                    .ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.AlreadyCompleted, managedVerification.Status);

            case DeleteOutcome.Changed:
                await _importWrites.MarkSourceCleanupChangedAsync(
                    obligation,
                    $"Source file changed: {singleError}",
                    CancellationToken.None).ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.SourceChanged);

            case DeleteOutcome.Denied:
                await _importWrites.MarkSourceCleanupFailedAsync(
                    obligation,
                    "Source cleanup access was denied.",
                    CancellationToken.None).ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.AccessDenied);

            default:
                await _importWrites.MarkSourceCleanupFailedAsync(
                    obligation,
                    "Source cleanup is temporarily blocked.",
                    CancellationToken.None).ConfigureAwait(false);
                return new StorageOperationResult(StorageOperationStatus.FileLocked);
        }
    }

    private bool IsWithinVault(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path)
                && RootPathRules.IsWithinOrEqual(_paths.Root, Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unresolvable path is not proven Vault-owned here; the existing identity/open/delete
            // checks below will refuse unsafe mutation rather than fabricating a successful cleanup.
            return false;
        }
    }

    private enum DeleteOutcome
    {
        Consumed,
        Missing,
        Changed,
        Denied,
        Locked,
        Failed
    }

    private static async Task<(DeleteOutcome Outcome, string? ErrorDetail)> SecureDeleteSourceFileAsync(
        string filePath,
        long expectedByteLength,
        string expectedSha256,
        string? expectedIdentityJson,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return (DeleteOutcome.Missing, null);
        }

        // X10: destructive MOVE requires stable object identity. No identity means preserve/fail
        // closed; content equality alone never authorizes pathname deletion.
        if (string.IsNullOrWhiteSpace(expectedIdentityJson))
        {
            return (DeleteOutcome.Changed, "Stable source file identity is unavailable.");
        }

        FileStream? stream = null;
        try
        {
            if (!SourceIdentityHelper.TryOpenForVerifiedDelete(
                    filePath,
                    out var deleteHandle,
                    out var openError)
                || deleteHandle is null)
            {
                return openError switch
                {
                    2 or 3 => (DeleteOutcome.Missing, "Source disappeared before verified deletion."),
                    5 => (DeleteOutcome.Denied, "DELETE access was denied opening the source file."),
                    32 or 33 => (DeleteOutcome.Locked, "Source file is locked against verified deletion."),
                    _ => (DeleteOutcome.Failed, $"Opening the source with DELETE authority failed with Win32 error {openError}."),
                };
            }

            try
            {
                stream = new FileStream(
                    deleteHandle,
                    FileAccess.Read,
                    bufferSize: 64 * 1024,
                    isAsync: false);
            }
            catch (UnauthorizedAccessException)
            {
                deleteHandle.Dispose();
                return (DeleteOutcome.Denied, "Access denied opening source file.");
            }
            catch (IOException)
            {
                deleteHandle.Dispose();
                return (DeleteOutcome.Locked, "Source file is locked.");
            }

            if (stream.Length != expectedByteLength)
            {
                return (DeleteOutcome.Changed, $"Byte length {stream.Length} != expected {expectedByteLength}.");
            }

            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var actualHash = Convert.ToHexStringLower(hashBytes);
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (DeleteOutcome.Changed, $"SHA256 {actualHash} != expected {expectedSha256}.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!SourceIdentityHelper.VerifyIdentity(
                    expectedIdentityJson,
                    stream.SafeFileHandle,
                    filePath))
            {
                return (DeleteOutcome.Changed, "Source file object identity changed since preparation.");
            }

            if (!SourceIdentityHelper.TryDeleteOpenedFile(stream.SafeFileHandle, out var deleteError))
            {
                return (DeleteOutcome.Locked, $"Handle-bound source deletion failed with Win32 error {deleteError}.");
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            stream = null;

            if (File.Exists(filePath))
            {
                return (DeleteOutcome.Locked, "The verified source object remains visible after handle-bound deletion.");
            }

            return (DeleteOutcome.Consumed, null);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsDispositionAuthorized(PersistedSourceCleanupObligation obligation)
    {
        if (obligation.Disposition is ItemDisposition.Skipped or ItemDisposition.Invalid
            || obligation.CandidateRetirementReason is AssetRetirementReason.Skipped
                or AssetRetirementReason.Cancelled
                or AssetRetirementReason.Invalid)
        {
            return false;
        }

        var included = obligation.Disposition == ItemDisposition.Included
            && obligation.DuplicateDecision is null or DuplicateDecision.Include
            && obligation.ReusedAssetId is null
            && obligation.CandidateAssetId == obligation.ManagedAssetId
            && obligation.CandidateState == AssetState.Active;

        var reused = obligation.Disposition == ItemDisposition.Reused
            && obligation.DuplicateDecision == DuplicateDecision.Reuse
            && obligation.ReusedAssetId == obligation.ManagedAssetId
            && obligation.CandidateState == AssetState.Retired
            && obligation.CandidateRetirementReason == AssetRetirementReason.DedupReused;

        return included || reused;
    }

    internal static bool HasStableCommittedAuthority(PersistedSourceCleanupObligation obligation) =>
        IsSafeLibraryCommitState(obligation.LibraryCommitState)
        && IsDispositionAuthorized(obligation)
        && string.Equals(
            obligation.CandidateOriginalSourcePath,
            obligation.SourcePath,
            StringComparison.Ordinal)
        && obligation.CandidateSha256 is not null
        && obligation.CandidateByteLength is not null
        && obligation.ManagedAssetId is not null
        && obligation.ManagedAssetState == AssetState.Active
        && obligation.ExpectedSha256 is not null
        && obligation.ExpectedByteLength is not null
        && obligation.CurrentManagedRelativePath is not null
        && obligation.CurrentManagedFileName is not null;

    private static string CombineRelative(string directory, string fileName) =>
        $"{directory.TrimEnd('/', '\\')}/{fileName}";
}
