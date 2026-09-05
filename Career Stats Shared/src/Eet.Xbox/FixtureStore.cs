using System.Globalization;
using System.Reflection;
using Eet.Trackers.Core;

namespace Eet.Xbox;

/// <summary>
/// Where the raw API-shaped fixtures come from.
///
/// Two sources, in order. Disk first -- <c>Career Stats Shared/fixtures/xbox/</c>, found by
/// walking up from wherever the assembly happens to be running -- so the owner can edit a
/// fixture and see the change without rebuilding. Then the copies embedded in this
/// assembly, so a published single-file build with no source tree around it still works.
///
/// The embedded fallback is the reason "zero credentials" is a promise rather than a hope:
/// there is no arrangement of working directory, publish layout or missing checkout in
/// which the fixture path has nothing to serve.
/// </summary>
public sealed class FixtureStore
{
    private const string ResourcePrefix = "Eet.Xbox.Fixtures.";

    private readonly string? _directory;

    public FixtureStore(string? directory = null) => _directory = directory ?? Discover();

    /// <summary>The fixture directory in use, or null when only embedded copies are available.</summary>
    public string? Directory => _directory;

    /// <summary>Read a fixture by file name, e.g. <c>achievements-halo-infinite.json</c>.</summary>
    public async Task<string> ReadAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_directory is not null)
        {
            var path = Path.Combine(_directory, name);
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
        }

        var assembly = typeof(FixtureStore).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name);

        if (stream is null)
        {
            // Note the concatenation is inside the interpolation hole, not around it: the
            // string.Create(IFormatProvider, ...) overload only binds to a single
            // interpolated literal, and splitting one with + silently selects a different
            // overload that does not compile.
            var searched = _directory ?? "(no fixture directory found)";

            throw new TrackerException(
                string.Create(CultureInfo.InvariantCulture, $"The fixture \"{name}\" was not found."),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Looked in {searched} and in the fixtures embedded in Eet.Xbox. Set EET_FIXTURES_DIR to the Career Stats Shared/fixtures/xbox directory if it lives somewhere unusual."));
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every fixture name available, from either source.</summary>
    public IReadOnlyList<string> Names()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        if (_directory is not null && System.IO.Directory.Exists(_directory))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
            {
                names.Add(Path.GetFileName(path));
            }
        }

        foreach (var resource in typeof(FixtureStore).Assembly.GetManifestResourceNames())
        {
            if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                names.Add(resource[ResourcePrefix.Length..]);
            }
        }

        return names.ToList();
    }

    /// <summary>
    /// Walk up from the build output looking for <c>Career Stats Shared/fixtures/xbox</c>.
    /// Returns null if it is not there, which is not an error: the embedded copies are.
    /// </summary>
    private static string? Discover()
    {
        var configured = Environment.GetEnvironmentVariable("EET_FIXTURES_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitXbox = Path.Combine(configured, "xbox");
            if (System.IO.Directory.Exists(explicitXbox))
            {
                return explicitXbox;
            }

            if (System.IO.Directory.Exists(configured))
            {
                return configured;
            }
        }

        var start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var directory = string.IsNullOrEmpty(start)
            ? new DirectoryInfo(AppContext.BaseDirectory)
            : new DirectoryInfo(start);

        for (var i = 0; i < 12 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Career Stats Shared", "fixtures", "xbox");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(directory.FullName, "fixtures", "xbox");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
