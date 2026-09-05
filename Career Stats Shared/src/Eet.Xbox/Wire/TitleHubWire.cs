namespace Eet.Xbox.Wire;

/// <summary>
/// <c>titlehub.xboxlive.com/users/xuid({xuid})/titles/titlehistory/decoration/achievement,scid</c>.
///
/// Two quirks worth naming: the response key is <c>xuid</c> (singular) on some responses
/// and <c>xuids</c> (an array) on others, so neither is relied on here; and the decoration
/// segment is what makes <c>achievement</c> non-null -- ask without it and every title
/// comes back with no gamerscore at all.
/// </summary>
internal sealed record TitleHubResponse
{
    public IReadOnlyList<TitleHubTitle>? Titles { get; init; }
}

internal sealed record TitleHubTitle
{
    public string? TitleId { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    public string? DisplayImage { get; init; }

    public IReadOnlyList<string>? Devices { get; init; }

    public TitleHubAchievement? Achievement { get; init; }

    public TitleHubHistory? TitleHistory { get; init; }
}

internal sealed record TitleHubAchievement
{
    public int CurrentAchievements { get; init; }

    public int TotalAchievements { get; init; }

    public int CurrentGamerscore { get; init; }

    public int TotalGamerscore { get; init; }

    /// <summary>Already a percentage: 2.0 means 2%.</summary>
    public double ProgressPercentage { get; init; }
}

internal sealed record TitleHubHistory
{
    public string? LastTimePlayed { get; init; }
}
