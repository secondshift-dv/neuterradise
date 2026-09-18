using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.SystemServices.Jobs;
using static Neuterradise.App.SystemServices.Jobs.JobPriorityPolicy;

namespace Neuterradise.App.Import.Preparation;

/// <summary>
/// Minimal pre-Stage-1 admission plan. HashAsset is the only work permitted before canonical
/// materialization; metadata, preview and face work belong exclusively to Stage 2.
/// </summary>
public sealed record CandidatePreparationPlan(JobDefinition HashJob)
{
    public static CandidatePreparationPlan Create(
        Guid importItemId,
        Guid candidateAssetId,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        var hashJob = new JobDefinition(
            DeriveJobId(candidateAssetId, "HashAsset"),
            Kind: "HashAsset",
            Lane: JobLane.Cpu,
            State: JobState.Pending,
            Priority: PriorityBackground,
            OwnerType: "Asset",
            OwnerId: candidateAssetId,
            MaxAttempts: JobRetryPolicy.DefaultMaxAttempts,
            CheckpointJson: $"{{\"schemaVersion\":1,\"importItemId\":\"{importItemId:D}\",\"sourcePath\":\"{EscapeJson(sourcePath)}\"}}");

        return new CandidatePreparationPlan(hashJob);
    }

    private static Guid DeriveJobId(Guid ownerId, string jobKind)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{ownerId:D}:{jobKind}"));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
