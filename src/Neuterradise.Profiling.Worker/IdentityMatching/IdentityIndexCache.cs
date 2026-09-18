using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;

namespace Neuterradise.Profiling.Worker.IdentityMatching;

public sealed class IdentityIndexCache : IDisposable
{
    private readonly ConcurrentDictionary<string, IdentityIndex> _indexes = new();

    public int LoadedIndexCount => _indexes.Count;

    public IReadOnlyList<string> LoadedSpaceKeys => _indexes.Keys.Order(StringComparer.Ordinal).ToArray();

    public bool IsSpaceLoaded(string spaceKey) => _indexes.ContainsKey(spaceKey);

    public bool TryGet(string spaceKey, out IdentityIndex? index) => _indexes.TryGetValue(spaceKey, out index);

    public IdentityIndex GetOrBuild(
        string spaceKey,
        string modelId,
        string modelVersion,
        IReadOnlyList<IdentitySampleData> samples)
    {
        var built = IdentityIndex.Build(spaceKey, modelId, modelVersion, samples, out _);
        return _indexes.AddOrUpdate(
            spaceKey,
            built,
            (_, _) => built);
    }

    public bool Invalidate(string spaceKey)
    {
        return _indexes.TryRemove(spaceKey, out _);
    }

    public void InvalidateAll()
    {
        _indexes.Clear();
    }

    public void Dispose()
    {
        _indexes.Clear();
    }
}
