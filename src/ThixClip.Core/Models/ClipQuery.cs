using MatCloud.Contract.Clip;

namespace ThixClip.Core.Models;

/// <summary>The parts of the history the sidebar offers.</summary>
public enum ClipSection
{
    History = 0,
    Images = 1,
    Favorites = 2,
}

public enum ClipSort
{
    NewestFirst = 0,
    OldestFirst = 1,
}

/// <summary>What the list shows: section (sidebar), search text, type filter and order (toolbar).</summary>
public sealed record ClipQuery(
    ClipSection Section = ClipSection.History,
    string Search = "",
    ClipKind? Kind = null,
    ClipSort Sort = ClipSort.NewestFirst);
