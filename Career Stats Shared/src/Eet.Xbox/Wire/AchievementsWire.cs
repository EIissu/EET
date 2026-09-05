namespace Eet.Xbox.Wire;

/// <summary>
/// <c>achievements.xboxlive.com</c>, contract version 2. Property names are camelCase on
/// the wire and matched case-insensitively here.
///
/// Shape checked against captured responses from the live service rather than written from
/// memory -- in particular <c>progressState</c> is the string <c>"Achieved"</c> (not a
/// boolean), gamerscore lives inside <c>rewards</c> as a STRING value with
/// <c>type: "Gamerscore"</c>, and <c>rarity</c> is present on modern titles but absent on
/// older ones, so it is nullable here.
/// </summary>
internal sealed record AchievementsResponse
{
    public IReadOnlyList<AchievementWire>? Achievements { get; init; }

    public PagingInfo? PagingInfo { get; init; }
}

internal sealed record PagingInfo
{
    /// <summary>
    /// Present when there are more achievements than one page. The service pages at 32 by
    /// default, which is fewer than most titles have, so following this is not optional.
    /// </summary>
    public string? ContinuationToken { get; init; }

    public int TotalRecords { get; init; }
}

internal sealed record AchievementWire
{
    public string? Id { get; init; }

    public string? ServiceConfigId { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<TitleAssociation>? TitleAssociations { get; init; }

    /// <summary><c>Achieved</c>, <c>InProgress</c> or <c>NotStarted</c>.</summary>
    public string? ProgressState { get; init; }

    public AchievementProgression? Progression { get; init; }

    public IReadOnlyList<MediaAsset>? MediaAssets { get; init; }

    public bool IsSecret { get; init; }

    /// <summary>What the achievement means once you have it.</summary>
    public string? Description { get; init; }

    /// <summary>How to get it. Blank for secret achievements, which is the point of them.</summary>
    public string? LockedDescription { get; init; }

    public IReadOnlyList<AchievementReward>? Rewards { get; init; }

    public bool IsRevoked { get; init; }

    public AchievementRarity? Rarity { get; init; }
}

internal sealed record TitleAssociation
{
    public string? Name { get; init; }

    /// <summary>Numeric on the wire; the rest of the codebase treats title ids as strings.</summary>
    public long Id { get; init; }
}

internal sealed record AchievementProgression
{
    public IReadOnlyList<AchievementRequirement>? Requirements { get; init; }

    /// <summary>
    /// UTC unlock time -- but only meaningful when <c>progressState</c> is
    /// <c>Achieved</c>. Locked achievements carry <c>0001-01-01T00:00:00.0000000</c>.
    /// </summary>
    public string? TimeUnlocked { get; init; }
}

internal sealed record AchievementRequirement
{
    public string? Id { get; init; }

    /// <summary>Strings, not numbers, and sometimes null on a never-started requirement.</summary>
    public string? Current { get; init; }

    public string? Target { get; init; }

    public string? ValueType { get; init; }
}

internal sealed record MediaAsset
{
    public string? Name { get; init; }

    /// <summary><c>Icon</c> is the only type that matters for a dashboard.</summary>
    public string? Type { get; init; }

    public string? Url { get; init; }
}

internal sealed record AchievementReward
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>A string on the wire even when <c>valueType</c> says <c>Int</c>.</summary>
    public string? Value { get; init; }

    /// <summary><c>Gamerscore</c>, or <c>Art</c> / <c>InApp</c> for cosmetic rewards.</summary>
    public string? Type { get; init; }

    public string? ValueType { get; init; }
}

internal sealed record AchievementRarity
{
    /// <summary><c>Rare</c> or <c>Common</c>, as judged by Xbox rather than by us.</summary>
    public string? CurrentCategory { get; init; }

    /// <summary>Percentage of owners who have it. 4.5 means "4.5%", not "0.045".</summary>
    public double CurrentPercentage { get; init; }
}
