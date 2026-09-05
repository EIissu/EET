using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eet.Xbox;

/// <summary>What we keep between runs so the user signs in once rather than hourly.</summary>
public sealed record CachedRefreshToken
{
    [JsonPropertyName("refresh_token")] public required string RefreshToken { get; init; }

    /// <summary>Which client id minted it. A token from another app registration is useless.</summary>
    [JsonPropertyName("client_id")] public required string ClientId { get; init; }

    [JsonPropertyName("scope")] public string? Scope { get; init; }

    [JsonPropertyName("obtained_at")] public DateTimeOffset ObtainedAt { get; init; }
}

/// <summary>Somewhere to keep the refresh token between runs.</summary>
public interface IRefreshTokenStore
{
    Task<CachedRefreshToken?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(CachedRefreshToken token, CancellationToken ct = default);

    /// <summary>Forget it. Called when Azure AD says the token is no longer valid.</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// The refresh token on disk, under the user's local application data directory.
///
/// This file is a live credential, not a cache: anyone who reads it can mint Xbox tokens
/// for the signed-in account until it is revoked. So it is written with its permissions cut
/// down to the current user on both platforms -- an explicit, inheritance-disabled DACL on
/// Windows, mode 0600 on everything else -- and the containing directory is created the
/// same way.
///
/// The file name ends ".tokencache.json" for one reason: that is a pattern the repository
/// .gitignore already covers, at any depth. <see cref="XboxOptions.TokenCachePath"/> and
/// EET_XBOX_TOKEN_CACHE both invite pointing this somewhere else, and "somewhere else" is
/// occasionally a working tree -- at which point the difference between a file name git
/// ignores and one it does not is the difference between a private credential and a
/// published one. A name that only the default path makes safe is not safe.
/// </summary>
public sealed class RefreshTokenStore : IRefreshTokenStore
{
    private readonly string _path;

    public RefreshTokenStore(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>Where this store reads and writes.</summary>
    public string Location => _path;

    /// <summary>
    /// The file name, kept as a constant because two things depend on it being exactly
    /// this: the .gitignore rule "*.tokencache.json", and the test that checks it.
    /// </summary>
    public const string FileName = "xbox-refresh-token.tokencache.json";

    /// <summary>LOCALAPPDATA/eet-trackers/xbox-refresh-token.tokencache.json, or the XDG equivalent.</summary>
    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrEmpty(root))
        {
            // LocalApplicationData comes back empty on some minimal Linux containers.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = Path.Combine(
                string.IsNullOrEmpty(home) ? Path.GetTempPath() : home,
                ".local",
                "share");
        }

        return Path.Combine(root, "eet-trackers", FileName);
    }

    public async Task<CachedRefreshToken?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CachedRefreshToken>(json, XboxJson.Read);
        }
        catch (JsonException)
        {
            // A corrupt cache is not worth failing a sign-in over; treat it as absent and
            // fall back to an interactive device code sign-in.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(CachedRefreshToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            RestrictToCurrentUser(directory, isDirectory: true);
        }

        // Create the file empty first, then narrow its permissions, then write the token
        // into it. Writing first would leave a live credential briefly world-readable.
        if (!File.Exists(_path))
        {
            await File.WriteAllTextAsync(_path, string.Empty, ct).ConfigureAwait(false);
        }

        RestrictToCurrentUser(_path, isDirectory: false);

        var json = JsonSerializer.Serialize(token, XboxJson.Write);
        await File.WriteAllTextAsync(_path, json, ct).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Best effort. A stale file that cannot be deleted is caught on the next load,
            // because Azure AD rejects the token it holds and we fall back to sign-in.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cut the permissions down to the current user. Windows gets an explicit,
    /// inheritance-disabled DACL; everything else gets 0600, or 0700 for the directory.
    /// </summary>
    private static void RestrictToCurrentUser(string path, bool isDirectory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictOnWindows(path, isDirectory);
            }
            else
            {
                var mode = isDirectory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite;

                File.SetUnixFileMode(path, mode);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Tightening failed, but the write itself still must not. The local application
            // data directory is already user-scoped, so this is defence in depth rather
            // than the only protection.
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictOnWindows(string path, bool isDirectory)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
        {
            return;
        }

        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"RefreshTokenStore({_path})");
}

/// <summary>
/// A store that keeps nothing. Used by the fixture path, and by tests, which must never
/// write a credential into a developer's real profile directory.
/// </summary>
public sealed class NullRefreshTokenStore : IRefreshTokenStore
{
    public Task<CachedRefreshToken?> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult<CachedRefreshToken?>(null);

    public Task SaveAsync(CachedRefreshToken token, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}
