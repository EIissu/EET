namespace Eet.Halo.Client.Http;

/// <summary>
/// A cap on how many requests are in flight against 343 at once.
///
/// Deliberately sits inside <see cref="HaloResilienceHandler"/> rather than outside it: the
/// slot is released while a retry is sleeping off a 429, so a throttled request does not
/// also block three healthy ones from making progress. Being polite about volume and being
/// polite about rate are different problems and this only solves the first.
/// </summary>
public sealed class HaloConcurrencyHandler : DelegatingHandler
{
    private readonly SemaphoreSlim _slots;

    public HaloConcurrencyHandler(int maxConcurrentRequests)
    {
        var permitted = Math.Max(1, maxConcurrentRequests);
        _slots = new SemaphoreSlim(permitted, permitted);
        Limit = permitted;
    }

    public int Limit { get; }

    /// <summary>Highest number of simultaneous in-flight requests observed. Test-visible.</summary>
    public int PeakInFlight => _peak;

    private int _inFlight;
    private int _peak;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var current = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _peak, current);
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _slots.Release();
        }
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (candidate <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, seen) != seen);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _slots.Dispose();
        }

        base.Dispose(disposing);
    }
}
