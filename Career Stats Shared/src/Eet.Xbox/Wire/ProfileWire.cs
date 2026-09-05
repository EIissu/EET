namespace Eet.Xbox.Wire;

/// <summary>
/// <c>profile.xboxlive.com/users/gt({gamertag})/profile/settings</c>, contract version 3.
///
/// The settings arrive as an unordered list of id/value pairs rather than named fields, so
/// everything is looked up by id. <c>id</c> on the user object is the XUID, which is the
/// value the rest of this tracker keys on -- see the homoglyph note in
/// <c>Eet.Trackers.Core.Identity</c> for why a gamertag is not good enough.
/// </summary>
internal sealed record ProfileResponse
{
    public IReadOnlyList<ProfileUser>? ProfileUsers { get; init; }
}

internal sealed record ProfileUser
{
    /// <summary>The XUID.</summary>
    public string? Id { get; init; }

    public string? HostId { get; init; }

    public IReadOnlyList<ProfileSetting>? Settings { get; init; }
}

internal sealed record ProfileSetting
{
    /// <summary><c>Gamertag</c>, <c>GameDisplayPicRaw</c>, <c>Gamerscore</c>, and many more.</summary>
    public string? Id { get; init; }

    public string? Value { get; init; }
}
