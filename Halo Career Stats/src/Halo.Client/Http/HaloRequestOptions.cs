using Eet.Halo.Client.Endpoints;

namespace Eet.Halo.Client.Http;

/// <summary>
/// How a <see cref="HaloCall"/> rides along with the <see cref="HttpRequestMessage"/> it
/// produced, so the delegating handlers can see which endpoint they are serving without
/// re-parsing the URL.
/// </summary>
public static class HaloRequestOptions
{
    public static readonly HttpRequestOptionsKey<HaloEndpoint> Endpoint = new("halo.endpoint");

    public static readonly HttpRequestOptionsKey<HaloCachePolicy> Cache = new("halo.cache");

    public static readonly HttpRequestOptionsKey<string> CacheKey = new("halo.cacheKey");

    public static HttpRequestMessage ToRequest(this HaloCall call)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, call.Uri);
        request.Options.Set(Endpoint, call.Endpoint);
        request.Options.Set(Cache, call.Cache);
        request.Options.Set(CacheKey, call.CacheKey);
        return request;
    }

    public static HaloEndpoint? GetEndpoint(this HttpRequestMessage request) =>
        request.Options.TryGetValue(Endpoint, out var endpoint) ? endpoint : null;

    public static HaloCachePolicy GetCachePolicy(this HttpRequestMessage request) =>
        request.Options.TryGetValue(Cache, out var policy) ? policy : HaloCachePolicy.None;

    public static string? GetCacheKey(this HttpRequestMessage request) =>
        request.Options.TryGetValue(CacheKey, out var key) ? key : null;
}
