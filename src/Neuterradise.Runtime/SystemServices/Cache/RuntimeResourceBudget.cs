
namespace Neuterradise.App.SystemServices.Cache;

public sealed class RuntimeResourceBudget
{
    public const long MinSoftBudgetBytes = 128L * 1024 * 1024;
    public const long MaxSoftBudgetBytes = 512L * 1024 * 1024;
    public const long MaxHardBudgetBytes = 768L * 1024 * 1024;
    public const long FallbackSoftBudgetBytes = 256L * 1024 * 1024;
    public const long RequiredBitmapMemoryBudgetBytes = 256L * 1024 * 1024;
    public const long FallbackHardBudgetBytes = 384L * 1024 * 1024;

    public const long HoverReservationBytes = 32L * 1024 * 1024;
    public const long VideoPlayerReservationBytes = 128L * 1024 * 1024;
    public const long ModelInteractiveReservationBytes = 256L * 1024 * 1024;

    public static readonly TimeSpan IdleEvictionThreshold = TimeSpan.FromMinutes(2);

    private readonly object _syncLock = new();
    private long _reservedScopeBytes;
    private long _trackedResourceBytes;

    public RuntimeResourceBudget(long? explicitTotalAvailableMemoryBytes = null)
    {
        var totalAvailable = explicitTotalAvailableMemoryBytes ?? GetSystemTotalAvailableBytes();
        var (soft, hard) = ComputeBudgets(totalAvailable);
        SoftBudgetBytes = soft;
        HardBudgetBytes = hard;
    }

    public long SoftBudgetBytes { get; }

    public long HardBudgetBytes { get; }

    public long ReservedScopeBytes => Interlocked.Read(ref _reservedScopeBytes);

    public long TrackedResourceBytes => Interlocked.Read(ref _trackedResourceBytes);

    public long TotalCommittedBytes => ReservedScopeBytes + TrackedResourceBytes;

    public bool IsAboveSoftBudget => TotalCommittedBytes >= SoftBudgetBytes;

    public bool IsAboveHardBudget => TotalCommittedBytes >= HardBudgetBytes;

    public static (long SoftBytes, long HardBytes) ComputeBudgets(long totalAvailableBytes)
    {
        if (totalAvailableBytes <= 0)
        {
            return (FallbackSoftBudgetBytes, FallbackHardBudgetBytes);
        }

        var soft = Math.Clamp(totalAvailableBytes / 10, MinSoftBudgetBytes, MaxSoftBudgetBytes);
        var hard = Math.Min(MaxHardBudgetBytes, (long)(soft * 1.5));
        return (soft, hard);
    }

    public static long EstimateBitmapBytes(int pixelWidth, int pixelHeight, int bitsPerPixel = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelHeight);

        checked
        {
            var stride = (((long)pixelWidth * bitsPerPixel) + 7) / 8;
            return pixelHeight * stride;
        }
    }

    public static long EstimateModelBytes(long adapterDeclaredBytes)
    {
        if (adapterDeclaredBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adapterDeclaredBytes),
                adapterDeclaredBytes,
                "Model resource size estimate must be strictly positive to be admitted into shared LRU cache.");
        }

        return adapterDeclaredBytes;
    }

    public IDisposable ReserveScopeBudget(long bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Reserved scope bytes must be positive.");
        }

        Interlocked.Add(ref _reservedScopeBytes, bytes);
        return new ScopeReservation(this, bytes);
    }

    internal void AddTrackedBytes(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _trackedResourceBytes, bytes);
        }
    }

    internal void RemoveTrackedBytes(long bytes)
    {
        if (bytes > 0)
        {
            lock (_syncLock)
            {
                _trackedResourceBytes = Math.Max(0, _trackedResourceBytes - bytes);
            }
        }
    }

    private void ReleaseScopeBudget(long bytes)
    {
        lock (_syncLock)
        {
            _reservedScopeBytes = Math.Max(0, _reservedScopeBytes - bytes);
        }
    }

    private static long GetSystemTotalAvailableBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            return info.TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class ScopeReservation : IDisposable
    {
        private readonly RuntimeResourceBudget _budget;
        private readonly long _bytes;
        private int _disposed;

        public ScopeReservation(RuntimeResourceBudget budget, long bytes)
        {
            _budget = budget;
            _bytes = bytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _budget.ReleaseScopeBudget(_bytes);
            }
        }
    }
}
