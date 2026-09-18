namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateTrustConfiguration(
    string? ConfiguredFeed,
    bool AllowExplicitLocalPackage)
{
    // Compatibility overload for the current source call site. v0.0.1 deliberately has no
    // publisher-key verification contract; a non-empty legacy value is therefore rejected.
    public UpdateTrustConfiguration(
        string? configuredFeed,
        string? legacyPublisherKeyId,
        bool allowExplicitLocalPackage)
        : this(configuredFeed, allowExplicitLocalPackage)
    {
        if (!string.IsNullOrWhiteSpace(legacyPublisherKeyId))
        {
            throw new ArgumentException(
                "Publisher-key verification is not part of the v0.0.1 update trust contract.",
                nameof(legacyPublisherKeyId));
        }
    }

    public bool HasConfiguredFeed => TryGetConfiguredFeed(out _);

    public bool TryGetConfiguredFeed(out Uri feed)
    {
        feed = null!;
        if (string.IsNullOrWhiteSpace(ConfiguredFeed)
            || !Uri.TryCreate(ConfiguredFeed, UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        feed = candidate;
        return true;
    }

    public bool MatchesConfiguredFeed(Uri actualFeed)
    {
        ArgumentNullException.ThrowIfNull(actualFeed);
        return TryGetConfiguredFeed(out var configuredFeed)
            && Uri.Compare(
                configuredFeed,
                actualFeed,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.Ordinal) == 0;
    }
}

public sealed record UpdateTrustDecision(
    bool Accepted,
    bool IntegrityVerified,
    bool IsExplicitLocalSource,
    string Reason)
{
    // Compatibility diagnostic only. v0.0.1 does not claim publisher identity verification.
    public bool PublisherVerified => false;

    public static UpdateTrustDecision Reject(string reason) => new(false, false, false, reason);
}

public sealed class UpdateTrustPolicy
{
    public UpdateTrustDecision Evaluate(UpdateManifest manifest, UpdateTrustConfiguration configuration, bool isLocalPackage, bool userConfirmedLocalPackage)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            manifest.Validate();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return UpdateTrustDecision.Reject(exception.Message);
        }

        if (!string.Equals(manifest.RuntimeIdentifier, ProductIdentity.RuntimeId, StringComparison.OrdinalIgnoreCase))
            return UpdateTrustDecision.Reject("Update runtime is incompatible.");
        if (!IsNewerVersion(manifest.ProductVersion, ProductIdentity.Version))
            return UpdateTrustDecision.Reject("Update version is not newer than the installed product.");

        if (isLocalPackage)
        {
            if (!configuration.AllowExplicitLocalPackage || !userConfirmedLocalPackage)
                return UpdateTrustDecision.Reject("Local update requires explicit confirmation.");

            return new UpdateTrustDecision(
                Accepted: true,
                IntegrityVerified: false,
                IsExplicitLocalSource: true,
                Reason: "Local update metadata is accepted by explicit user confirmation; payload integrity must pass before staging or apply.");
        }

        if (!configuration.HasConfiguredFeed)
            return UpdateTrustDecision.Reject("No valid configured HTTPS update feed is available.");

        return new UpdateTrustDecision(
            Accepted: true,
            IntegrityVerified: false,
            IsExplicitLocalSource: false,
            Reason: "Update metadata is accepted from the configured HTTPS feed; payload integrity must pass before staging or apply.");
    }

    private static bool IsNewerVersion(string candidate, string installed) =>
        Version.TryParse(candidate, out var candidateVersion)
        && Version.TryParse(installed, out var installedVersion)
        && candidateVersion > installedVersion;
}
