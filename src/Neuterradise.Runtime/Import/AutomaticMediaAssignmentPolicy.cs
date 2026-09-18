namespace Neuterradise.App.Import;

/// <summary>
/// Single authority for automatic import ownership decisions. Only explicit context, existing durable
/// ownership, or another deterministic domain fact may become authoritative. Partial/conflicting
/// evidence stays unresolved and is grouped for one human exception decision rather than guessed.
/// </summary>
public sealed class AutomaticMediaAssignmentPolicy
{
    public ImportAssignmentDecision Resolve(Guid? explicitProfileId, Guid? durableOwnerProfileId)
    {
        if (explicitProfileId is { } explicitId && explicitId != Guid.Empty)
        {
            return ImportAssignmentDecision.Authoritative(explicitId, ImportAssignmentEvidence.ExplicitContext);
        }

        if (durableOwnerProfileId is { } ownerId && ownerId != Guid.Empty)
        {
            return ImportAssignmentDecision.Authoritative(ownerId, ImportAssignmentEvidence.DurableRelationship);
        }

        return ImportAssignmentDecision.Unknown;
    }

    public ImportAssignmentAssessment EvaluateBatch(IEnumerable<ImportAssignmentEvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var items = evidence.ToList();
        if (items.Count == 0)
        {
            return new ImportAssignmentAssessment(ImportAssignmentDecision.Unknown, [], 0, 0);
        }

        var grouped = items.GroupBy(
            item => Normalize(item.SourceDirectory),
            StringComparer.OrdinalIgnoreCase);
        var clusters = grouped
            .Select(group =>
            {
                var candidates = group
                    .Select(item => item.DurableOwnerProfileId)
                    .Where(id => id is { } value && value != Guid.Empty)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToArray();
                var candidate = candidates.Length == 1 ? candidates[0] : (Guid?)null;
                var evidence = candidates.Length switch
                {
                    0 => ImportAssignmentClusterEvidence.Insufficient,
                    1 => ImportAssignmentClusterEvidence.HeuristicCandidate,
                    _ => ImportAssignmentClusterEvidence.Conflicting,
                };
                var key = $"{group.Key}|{candidate?.ToString("D") ?? evidence.ToString()}";
                return new ImportAssignmentCluster(
                    key,
                    group.Key == "unknown" ? null : group.First().SourceDirectory,
                    candidate,
                    evidence,
                    group.Select(item => item.ItemId).ToArray());
            })
            .OrderByDescending(cluster => cluster.ItemIds.Count)
            .ThenBy(cluster => cluster.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var supported = items.Count(item => item.DurableOwnerProfileId is { } id && id != Guid.Empty);
        var distinctOwners = items
            .Select(item => item.DurableOwnerProfileId)
            .Where(id => id is { } value && value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        // Applying one owner to the entire import batch is safe only when every item has the same
        // durable owner evidence. A single matching duplicate is not allowed to classify 2,999
        // unrelated files merely because they arrived in the same picker action.
        var decision = supported == items.Count && distinctOwners.Length == 1
            ? ImportAssignmentDecision.Authoritative(
                distinctOwners[0],
                ImportAssignmentEvidence.DurableRelationship)
            : ImportAssignmentDecision.Unknown;

        return new ImportAssignmentAssessment(decision, clusters, items.Count, supported);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().TrimEnd('\\', '/');
}

public sealed record ImportAssignmentEvidenceItem(
    Guid ItemId,
    string? SourceDirectory,
    Guid? DurableOwnerProfileId);

public sealed record ImportAssignmentCluster(
    string Key,
    string? SourceDirectory,
    Guid? CandidateProfileId,
    ImportAssignmentClusterEvidence Evidence,
    IReadOnlyList<Guid> ItemIds);

public enum ImportAssignmentClusterEvidence
{
    HeuristicCandidate,
    Conflicting,
    Insufficient
}

public sealed record ImportAssignmentAssessment(
    ImportAssignmentDecision Decision,
    IReadOnlyList<ImportAssignmentCluster> Clusters,
    int TotalItemCount,
    int SupportedItemCount)
{
    public bool HasAmbiguity => !Decision.IsAuthoritative && Clusters.Count > 0;
}

public enum ImportAssignmentEvidence
{
    Unknown,
    ExplicitContext,
    DurableRelationship,
    DeterministicCandidate,
    HeuristicCandidate
}

public sealed record ImportAssignmentDecision(Guid? ProfileId, ImportAssignmentEvidence Evidence)
{
    public bool IsAuthoritative => ProfileId is not null
        && Evidence is ImportAssignmentEvidence.ExplicitContext
            or ImportAssignmentEvidence.DurableRelationship
            or ImportAssignmentEvidence.DeterministicCandidate;

    public static ImportAssignmentDecision Unknown { get; } =
        new(null, ImportAssignmentEvidence.Unknown);

    public static ImportAssignmentDecision Authoritative(Guid profileId, ImportAssignmentEvidence evidence) =>
        new(profileId, evidence);
}
