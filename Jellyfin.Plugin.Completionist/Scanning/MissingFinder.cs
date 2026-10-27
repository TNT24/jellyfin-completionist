using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Completionist.Scanning;

/// <summary>An episode as an episode guide lists it.</summary>
/// <param name="Season">Season number (0 for specials).</param>
/// <param name="Episode">Episode number within the season.</param>
/// <param name="Title">Episode title, if known.</param>
/// <param name="AirDate">First air date (UTC), if known.</param>
public sealed record GuideEpisode(int Season, int Episode, string? Title, DateTime? AirDate);

/// <summary>An episode file in the library.</summary>
/// <param name="Season">Season number.</param>
/// <param name="Episode">First episode number in the file.</param>
/// <param name="EpisodeEnd">Last episode number for multi-episode files (S01E01-E02), otherwise null.</param>
public sealed record LocalEpisode(int Season, int Episode, int? EpisodeEnd = null);

/// <summary>What counts as missing.</summary>
/// <param name="IncludeSpecials">Whether season 0 counts.</param>
/// <param name="ReportWholeSeasons">Whether seasons with no local episodes are reported.</param>
/// <param name="AiredBefore">Episodes must have aired before this time (UTC) to count.</param>
public sealed record FinderOptions(bool IncludeSpecials, bool ReportWholeSeasons, DateTime AiredBefore);

/// <summary>Result of comparing one series with its guide.</summary>
public sealed class FinderResult
{
    /// <summary>Gets seasons that have aired but have no episodes in the library, with their aired episode count.</summary>
    public List<(int Season, int AiredCount)> MissingSeasons { get; } = [];

    /// <summary>Gets single missing episodes in seasons that are partly in the library.</summary>
    public List<GuideEpisode> MissingEpisodes { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the library's numbering doesn't line up with the guide at all,
    /// so the result can't be trusted (anime in absolute order, seasons numbered by year, wrong show matched).
    /// </summary>
    public bool NumberingMismatch { get; set; }
}

/// <summary>
/// Works out what's missing from one series. No Jellyfin or network access, so it can be unit tested.
/// </summary>
public static class MissingFinder
{
    /// <summary>Compares the library's episodes with an episode guide.</summary>
    /// <param name="local">Episodes in the library.</param>
    /// <param name="guide">Episodes in the guide.</param>
    /// <param name="options">What counts as missing.</param>
    /// <returns>The missing seasons and episodes.</returns>
    public static FinderResult Compare(IReadOnlyCollection<LocalEpisode> local, IReadOnlyList<GuideEpisode> guide, FinderOptions options)
    {
        var result = new FinderResult();
        var have = Expand(local);
        var seasonsWithFiles = have.Select(k => k.Season).ToHashSet();

        // If not one file lines up with the guide, the numbering schemes differ and everything would look missing.
        var regularHave = have.Where(k => k.Season > 0).ToList();
        if (regularHave.Count > 0)
        {
            var guideKeys = guide.Select(e => (e.Season, e.Episode)).ToHashSet();
            if (!regularHave.Any(guideKeys.Contains))
            {
                result.NumberingMismatch = true;
                return result;
            }
        }

        var aired = Aired(guide, options.AiredBefore)
            .Where(e => options.IncludeSpecials || e.Season > 0)
            .ToList();

        foreach (var season in aired.GroupBy(e => e.Season).OrderBy(g => g.Key))
        {
            if (!seasonsWithFiles.Contains(season.Key))
            {
                if (options.ReportWholeSeasons)
                {
                    result.MissingSeasons.Add((season.Key, season.Count()));
                }

                continue;
            }

            result.MissingEpisodes.AddRange(season.Where(e => !have.Contains((e.Season, e.Episode))).OrderBy(e => e.Episode));
        }

        return result;
    }

    /// <summary>
    /// Finds holes in the library's own numbering, for shows no guide knows: within each season,
    /// any number between 1 and the highest one present that has no file.
    /// </summary>
    /// <param name="local">Episodes in the library.</param>
    /// <param name="includeSpecials">Whether season 0 counts.</param>
    /// <returns>The missing episodes, without titles or dates.</returns>
    public static List<GuideEpisode> FindGaps(IReadOnlyCollection<LocalEpisode> local, bool includeSpecials)
    {
        var have = Expand(local);
        var missing = new List<GuideEpisode>();
        foreach (var season in have.GroupBy(k => k.Season).OrderBy(g => g.Key))
        {
            if (season.Key == 0 && !includeSpecials)
            {
                continue;
            }

            var max = season.Max(k => k.Episode);
            for (var e = 1; e < max; e++)
            {
                if (!have.Contains((season.Key, e)))
                {
                    missing.Add(new GuideEpisode(season.Key, e, null, null));
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Finds the released films of a collection that aren't in the library. Films without a
    /// release date haven't been released yet as far as TMDb knows, so they never count.
    /// </summary>
    /// <param name="parts">The collection's films.</param>
    /// <param name="owned">TMDb ids of the films in the library.</param>
    /// <param name="ignored">TMDb ids of films the user ignored.</param>
    /// <param name="releasedBefore">Films must have been released before this time (UTC) to count.</param>
    /// <returns>The missing films, oldest first.</returns>
    public static List<CollectionPart> FindMissingFilms(IEnumerable<CollectionPart> parts, IReadOnlySet<int> owned, IReadOnlySet<int> ignored, DateTime releasedBefore)
        => parts
            .Where(p => p.ReleaseDate is DateTime d && d < releasedBefore && !owned.Contains(p.TmdbId) && !ignored.Contains(p.TmdbId))
            .DistinctBy(p => p.TmdbId)
            .OrderBy(p => p.ReleaseDate)
            .ToList();

    /// <summary>
    /// Turns a sorted list of episode numbers into ranges, e.g. 1,2,3,5 → "E01–E03, E05".
    /// </summary>
    /// <param name="episodes">Episode numbers.</param>
    /// <returns>(first, last) pairs.</returns>
    public static List<(int First, int Last)> ToRanges(IEnumerable<int> episodes)
    {
        var ranges = new List<(int First, int Last)>();
        foreach (var e in episodes.Distinct().Order())
        {
            if (ranges.Count > 0 && ranges[^1].Last == e - 1)
            {
                ranges[^1] = (ranges[^1].First, e);
            }
            else
            {
                ranges.Add((e, e));
            }
        }

        return ranges;
    }

    private static HashSet<(int Season, int Episode)> Expand(IEnumerable<LocalEpisode> local)
    {
        var have = new HashSet<(int Season, int Episode)>();
        foreach (var ep in local)
        {
            // A multi-episode file covers every number in its range; cap it so a bad end number can't run away.
            var end = ep.EpisodeEnd is int last && last > ep.Episode ? Math.Min(last, ep.Episode + 50) : ep.Episode;
            for (var e = ep.Episode; e <= end; e++)
            {
                have.Add((ep.Season, e));
            }
        }

        return have;
    }

    /// <summary>
    /// Episodes that have aired. Guides sometimes leave the date off old episodes, so an undated
    /// episode counts as aired when a later one in the guide already has.
    /// </summary>
    private static IEnumerable<GuideEpisode> Aired(IReadOnlyList<GuideEpisode> guide, DateTime airedBefore)
    {
        var ordered = guide
            .Where(e => e.Season > 0)
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode)
            .ToList();
        var lastAiredIndex = ordered.FindLastIndex(e => e.AirDate is DateTime d && d < airedBefore);
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            if (e.AirDate is DateTime d ? d < airedBefore : i < lastAiredIndex)
            {
                yield return e;
            }
        }

        // Specials aren't in airing order with the rest, so they need a date of their own.
        foreach (var e in guide.Where(e => e.Season == 0 && e.AirDate is DateTime d && d < airedBefore))
        {
            yield return e;
        }
    }
}
