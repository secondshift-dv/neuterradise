using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Neuterradise.App.SystemServices.Database;

using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.Faces;

public sealed class IdentityBankProvider
{
    private static readonly ConditionalWeakTable<CatalogDb, CatalogProviderSet> ProvidersByCatalog = new();

    private readonly FaceReads _reads;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _loadGate = new(initialCount: 1, maxCount: 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<EmbeddingSpaceKey, IdentityBankSpace> _loaded = [];
    private readonly Dictionary<EmbeddingSpaceKey, long> _spaceGenerations = [];

    private long _epoch;
    private long _spaceLoadCount;

    public IdentityBankProvider(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _reads = catalog.FaceReads;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ProvidersByCatalog.GetValue(catalog, static _ => new CatalogProviderSet()).Register(this);
    }

    public IdentityBankProvider(FaceReads reads, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reads);
        _reads = reads;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static void InvalidateCatalogSpace(CatalogDb catalog, EmbeddingSpaceKey space)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EmbeddingSpaceKey.EnsureValid(space, nameof(space));

        if (ProvidersByCatalog.TryGetValue(catalog, out var providers))
        {
            providers.Invalidate(space);
        }
    }

    public long SpaceLoadCount => Interlocked.Read(ref _spaceLoadCount);

    public IReadOnlyList<EmbeddingSpaceKey> LoadedSpaces
    {
        get
        {
            lock (_gate)
            {
                return _loaded.Keys
                    .OrderBy(static key => key.Canonical, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public bool IsSpaceLoaded(EmbeddingSpaceKey space)
    {
        if (!space.IsValid)
        {
            return false;
        }

        lock (_gate)
        {
            return _loaded.ContainsKey(space);
        }
    }

    public Task<EmbeddingSpaceInventory> ListPopulatedSpacesAsync(
        CancellationToken cancellationToken = default) =>
        _reads.ListPopulatedEmbeddingSpacesAsync(cancellationToken);

    public async Task<IdentityBankSpace> GetSpaceAsync(
        EmbeddingSpaceKey space,
        CancellationToken cancellationToken = default)
    {
        EmbeddingSpaceKey.EnsureValid(space, nameof(space));

        if (TryGetCached(space) is { } cached)
        {
            return cached;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(space) is { } raced)
            {
                return raced;
            }

            long epochAtStart;
            long generationAtStart;
            lock (_gate)
            {
                epochAtStart = _epoch;
                generationAtStart = GenerationOf(space);
            }

            var built = await BuildSpaceAsync(space, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_epoch == epochAtStart && GenerationOf(space) == generationAtStart)
                {
                    _loaded[space] = built;
                }
            }

            return built;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public bool InvalidateSpace(EmbeddingSpaceKey space)
    {
        EmbeddingSpaceKey.EnsureValid(space, nameof(space));

        lock (_gate)
        {
            _spaceGenerations[space] = GenerationOf(space) + 1;
            return _loaded.Remove(space);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            _epoch++;
            _loaded.Clear();
            _spaceGenerations.Clear();
        }
    }

    private IdentityBankSpace? TryGetCached(EmbeddingSpaceKey space)
    {
        lock (_gate)
        {
            return _loaded.GetValueOrDefault(space);
        }
    }

    private long GenerationOf(EmbeddingSpaceKey space) =>
        _spaceGenerations.TryGetValue(space, out var generation) ? generation : 0;

    private async Task<IdentityBankSpace> BuildSpaceAsync(
        EmbeddingSpaceKey space,
        CancellationToken cancellationToken)
    {
        var records = await _reads.LoadAuthoritativeSamplesAsync(space, cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Increment(ref _spaceLoadCount);

        var grouped = new Dictionary<Guid, List<IdentityBankSample>>();
        var profileByIdentity = new Dictionary<Guid, Guid>();
        var diagnostics = new List<IdentityBankDiagnostic>();

        foreach (var record in records)
        {
            if (!FaceEmbedding.TryFromBlob(record.Embedding, space, out var values, out var failure))
            {

                diagnostics.Add(new IdentityBankDiagnostic(
                    IdentityBankDiagnosticCodes.EmbeddingSchemaViolation,
                    record.IdentitySampleId,
                    failure ?? "The persisted embedding does not satisfy its space contract."));
                continue;
            }

            if (!grouped.TryGetValue(record.IdentityId, out var samples))
            {
                samples = [];
                grouped[record.IdentityId] = samples;
                profileByIdentity[record.IdentityId] = record.ProfileId;
            }

            samples.Add(new IdentityBankSample(
                record.IdentitySampleId,
                record.FaceId,
                record.AssetId,
                values));
        }

        var identities = grouped
            .Select(pair => new IdentityBankEntry(
                pair.Key,
                profileByIdentity[pair.Key],
                pair.Value
                    .OrderBy(static sample => sample.IdentitySampleId)
                    .ToArray()))
            .OrderBy(static entry => entry.IdentityId)
            .ToArray();

        return new IdentityBankSpace(
            space,
            identities,
            diagnostics,
            _timeProvider.GetUtcNow());
    }

    private sealed class CatalogProviderSet
    {
        private readonly Lock _gate = new();
        private readonly List<WeakReference<IdentityBankProvider>> _providers = [];

        public void Register(IdentityBankProvider provider)
        {
            lock (_gate)
            {
                _providers.RemoveAll(static reference => !reference.TryGetTarget(out _));
                _providers.Add(new WeakReference<IdentityBankProvider>(provider));
            }
        }

        public void Invalidate(EmbeddingSpaceKey space)
        {
            IdentityBankProvider[] live;
            lock (_gate)
            {
                var providers = new List<IdentityBankProvider>(_providers.Count);
                for (var index = _providers.Count - 1; index >= 0; index--)
                {
                    if (_providers[index].TryGetTarget(out var provider))
                    {
                        providers.Add(provider);
                    }
                    else
                    {
                        _providers.RemoveAt(index);
                    }
                }

                live = providers.ToArray();
            }

            foreach (var provider in live)
            {
                provider.InvalidateSpace(space);
            }
        }
    }
}

public sealed class IdentityBankSpace
{
    private readonly Dictionary<Guid, IdentityBankEntry> _byIdentityId;

    internal IdentityBankSpace(
        EmbeddingSpaceKey space,
        IReadOnlyList<IdentityBankEntry> identities,
        IReadOnlyList<IdentityBankDiagnostic> diagnostics,
        DateTimeOffset loadedAtUtc)
    {
        Space = space;
        Identities = identities;
        Diagnostics = diagnostics;
        LoadedAtUtc = loadedAtUtc;
        SampleCount = identities.Sum(static entry => entry.Samples.Count);
        _byIdentityId = identities.ToDictionary(static entry => entry.IdentityId);
    }

    public EmbeddingSpaceKey Space { get; }

    public IReadOnlyList<IdentityBankEntry> Identities { get; }

    public int IdentityCount => Identities.Count;

    public int SampleCount { get; }

    public IReadOnlyList<IdentityBankDiagnostic> Diagnostics { get; }

    public int ExcludedSampleCount => Diagnostics.Count;

    public DateTimeOffset LoadedAtUtc { get; }

    public bool TryGetIdentity(Guid identityId, [MaybeNullWhen(false)] out IdentityBankEntry entry) =>
        _byIdentityId.TryGetValue(identityId, out entry);

    public double? ScoreIdentity(
        EmbeddingSpaceKey probeSpace,
        ReadOnlySpan<float> probe,
        Guid identityId)
    {
        EmbeddingSpaceKey.EnsureCompatible(Space, probeSpace);

        if (!_byIdentityId.TryGetValue(identityId, out var entry) || entry.Samples.Count == 0)
        {
            return null;
        }

        var best = double.NegativeInfinity;
        foreach (var sample in entry.Samples)
        {
            var score = FaceEmbedding.CosineSimilarity(probeSpace, probe, Space, sample.Embedding.Span);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }
}

public sealed class IdentityBankEntry
{
    internal IdentityBankEntry(Guid identityId, Guid profileId, IReadOnlyList<IdentityBankSample> samples)
    {
        IdentityId = identityId;
        ProfileId = profileId;
        Samples = samples;
    }

    public Guid IdentityId { get; }

    public Guid ProfileId { get; }

    public IReadOnlyList<IdentityBankSample> Samples { get; }
}

public sealed record IdentityBankSample(
    Guid IdentitySampleId,
    Guid FaceId,
    Guid AssetId,
    ReadOnlyMemory<float> Embedding);

public static class IdentityBankDiagnosticCodes
{

    public const string EmbeddingSchemaViolation = "identity-bank.embedding-schema-violation";
}

public sealed record IdentityBankDiagnostic(
    string Code,
    Guid IdentitySampleId,
    string Detail)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Code} [{IdentitySampleId:D}]: {Detail}");
}
