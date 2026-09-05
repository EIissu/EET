using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Eet.Halo.Client.Http;

/// <summary>
/// An on-disk response cache, sitting outermost in the handler chain so a hit costs no
/// token, no retry budget and no concurrency slot.
///
/// The interesting policy is that <see cref="HaloCachePolicy.Forever"/> means forever.
/// A finished Halo match's stats document is immutable -- the game is over, nobody is
/// going to score again -- so re-fetching one is pure cost to 343's servers and pure
/// latency to us. That single fact is what makes a 120-match trend view cheap on the
/// second run: the history page is re-fetched, the 119 unchanged match documents are not.
/// </summary>
public sealed class HaloDiskCacheHandler : DelegatingHandler
{
    private readonly string? _directory;
    private readonly TimeSpan _shortLifetime;
    private readonly ILogger<HaloDiskCacheHandler> _logger;
    private readonly TimeProvider _time;

    public HaloDiskCacheHandler(
        string? directory,
        TimeSpan shortLifetime,
        ILogger<HaloDiskCacheHandler>? logger = null,
        TimeProvider? time = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        _shortLifetime = shortLifetime;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HaloDiskCacheHandler>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public int Hits => _hits;

    public int Misses => _misses;

    private int _hits;
    private int _misses;

    /// <summary>
    /// The default cache location: per-user, outside the repository, easy to delete.
    /// </summary>
    /// <remarks>
    /// The guard is not theoretical.
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an EMPTY
    /// string rather than throwing when it cannot resolve the folder -- a Unix account with
    /// no HOME, a service identity, a container -- and Path.Combine with an empty first
    /// segment yields a RELATIVE path. That would put the cache under whatever directory the
    /// process happens to be started from, which for `dotnet run` is inside the working
    /// tree. These files are recorded API responses: a full match history, keyed by XUID,
    /// belonging to a real person. They must never land somewhere a `git add .` can sweep
    /// them up.
    /// </remarks>
    public static string DefaultDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            {
                root = Path.GetTempPath();
            }

            return Path.Combine(root, "eet-trackers", "halo-cache");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var policy = request.GetCachePolicy();
        var key = request.GetCacheKey();
        if (_directory is null || policy == HaloCachePolicy.None || key is null || request.Method != HttpMethod.Get)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var file = PathFor(key);
        var cached = await TryReadAsync(file, policy, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            Interlocked.Increment(ref _hits);
            _logger.LogDebug("Halo cache hit for {Key}.", key);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cached, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }

        Interlocked.Increment(ref _misses);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content is not null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await TryWriteAsync(file, body, cancellationToken).ConfigureAwait(false);

            // The body has been consumed into a string; hand back a fresh response so the
            // caller can still read it.
            var replacement = new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            response.Dispose();
            return replacement;
        }

        return response;
    }

    private string PathFor(string key)
    {
        // Hash rather than sanitise: a cache key contains a full path and query, which is
        // longer than MAX_PATH allows and full of characters Windows will not accept.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory!, hash[..2], hash + ".json");
    }

    private async Task<string?> TryReadAsync(string file, HaloCachePolicy policy, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            if (policy == HaloCachePolicy.Short)
            {
                var age = _time.GetUtcNow() - File.GetLastWriteTimeUtc(file);
                if (age > _shortLifetime)
                {
                    return null;
                }
            }

            return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache is an optimisation. Losing one is not worth failing a request over.
            _logger.LogDebug(ex, "Halo cache read failed for {File}.", file);
            return null;
        }
    }

    private async Task TryWriteAsync(string file, string body, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Write-then-rename, so a cancelled run cannot leave a half-written document
            // that later parses as valid-but-truncated JSON.
            var temp = string.Create(CultureInfo.InvariantCulture, $"{file}.{Environment.ProcessId}.tmp");
            await File.WriteAllTextAsync(temp, body, ct).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Halo cache write failed for {File}.", file);
        }
    }
}
