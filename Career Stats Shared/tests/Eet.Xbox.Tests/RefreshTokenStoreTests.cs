using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Eet.Xbox.Tests;

/// <summary>
/// The refresh token on disk.
///
/// Every test here writes into a fresh temporary directory. None of them touch the real
/// LOCALAPPDATA path, because a test that overwrote a developer's live refresh token would
/// silently log them out of their own tool.
/// </summary>
public sealed class RefreshTokenStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "eet-xbox-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_directory, "xbox-refresh-token.json");

    [Fact]
    public async Task A_saved_token_round_trips()
    {
        var store = new RefreshTokenStore(Path_);
        var token = new CachedRefreshToken
        {
            RefreshToken = "a-refresh-token",
            ClientId = "00000000-0000-0000-0000-00000000c0de",
            Scope = XboxEndpoints.DefaultScope,
            ObtainedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
        };

        await store.SaveAsync(token);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(token.RefreshToken, loaded.RefreshToken);
        Assert.Equal(token.ClientId, loaded.ClientId);
        Assert.Equal(token.ObtainedAt, loaded.ObtainedAt);
    }

    [Fact]
    public async Task An_absent_file_loads_as_null_rather_than_throwing()
    {
        Assert.Null(await new RefreshTokenStore(Path_).LoadAsync());
    }

    [Fact]
    public async Task A_corrupt_file_loads_as_null_so_a_bad_cache_cannot_block_a_sign_in()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path_, "{ this is not json");

        // Falling back to an interactive sign-in is always recoverable. Throwing here would
        // leave the user unable to run the tool at all until they found and deleted a file
        // they do not know exists.
        Assert.Null(await new RefreshTokenStore(Path_).LoadAsync());
    }

    [Fact]
    public async Task Clearing_removes_the_credential_from_disk()
    {
        var store = new RefreshTokenStore(Path_);
        await store.SaveAsync(new CachedRefreshToken { RefreshToken = "r", ClientId = "c" });

        Assert.True(File.Exists(Path_));

        await store.ClearAsync();

        Assert.False(File.Exists(Path_));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Clearing_a_file_that_is_not_there_is_not_an_error()
    {
        await new RefreshTokenStore(Path_).ClearAsync();
    }

    [Fact]
    public async Task The_saved_file_is_readable_only_by_the_current_user()
    {
        var store = new RefreshTokenStore(Path_);
        await store.SaveAsync(new CachedRefreshToken { RefreshToken = "r", ClientId = "c" });

        if (OperatingSystem.IsWindows())
        {
            AssertWindowsAclIsCurrentUserOnly(Path_);
        }
        else
        {
            var mode = File.GetUnixFileMode(Path_);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void The_default_path_is_under_the_user_profile_not_the_working_directory()
    {
        var path = RefreshTokenStore.DefaultPath();

        // A token cache in the working directory is one `git add .` away from being
        // published. The .gitignore covers that case too, but not landing there in the
        // first place is the better defence.
        Assert.Contains("eet-trackers", path, StringComparison.Ordinal);
        Assert.NotEqual(Directory.GetCurrentDirectory(), Path.GetDirectoryName(path));
    }

    [Fact]
    public void The_default_cache_file_name_is_one_the_repository_gitignore_covers()
    {
        // The .gitignore rule is "*.tokencache.json", which git applies at any depth. The
        // option and EET_XBOX_TOKEN_CACHE both invite pointing this store somewhere else,
        // and "somewhere else" is occasionally a working tree -- so the safety has to live
        // in the file name rather than in the default directory. A name like
        // "xbox-refresh-token.json" is one `git add .` away from publishing a live
        // credential, and no permission bit on the file prevents that.
        Assert.EndsWith(".tokencache.json", RefreshTokenStore.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".tokencache.json", RefreshTokenStore.DefaultPath(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_null_store_keeps_nothing()
    {
        var store = new NullRefreshTokenStore();

        await store.SaveAsync(new CachedRefreshToken { RefreshToken = "r", ClientId = "c" });

        Assert.Null(await store.LoadAsync());
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsAclIsCurrentUserOnly(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var me = WindowsIdentity.GetCurrent().User;

        Assert.True(rules.Count > 0);

        foreach (FileSystemAccessRule rule in rules)
        {
            Assert.Equal(me, rule.IdentityReference);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
