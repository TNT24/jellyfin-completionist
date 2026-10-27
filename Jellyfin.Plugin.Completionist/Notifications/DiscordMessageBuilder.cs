using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Completionist.Scanning;

namespace Jellyfin.Plugin.Completionist.Notifications;

/// <summary>What changed in one series since the last check.</summary>
/// <param name="Series">The series' current report.</param>
/// <param name="Seasons">Whole seasons missing that weren't before.</param>
/// <param name="Episodes">Episodes missing that weren't before.</param>
public sealed record SeriesChange(SeriesReport Series, IReadOnlyList<MissingSeason> Seasons, IReadOnlyList<MissingEpisode> Episodes);

/// <summary>Films missing from a collection that weren't before.</summary>
/// <param name="Collection">The collection's current report.</param>
/// <param name="Films">The newly missing films.</param>
public sealed record CollectionChange(CollectionReport Collection, IReadOnlyList<MissingFilm> Films);

/// <summary>Things that were missing last time and are in the library now.</summary>
/// <param name="ItemId">The Jellyfin series or collection, if there is one.</param>
/// <param name="Name">Series or collection name.</param>
/// <param name="Labels">What turned up, e.g. "S02E05", "Season 3" or a film title.</param>
public sealed record ResolvedChange(Guid? ItemId, string Name, IReadOnlyList<string> Labels);

/// <summary>
/// Turns check results into Discord webhook messages. No network access, so it can be unit tested.
/// </summary>
public static class DiscordMessageBuilder
{
    // Discord limits: 10 embeds and 6000 characters per message, 4096 per embed description.
    private const int MaxEmbedsPerMessage = 10;
    private const int MaxCharsPerMessage = 5800;
    private const int MaxDescription = 3500;
    private const int ListEachEpisodeUpTo = 8;
    private const int ListFilmsUpTo = 15;

    private const int ColorNew = 0xF5A623;
    private const int ColorFilm = 0xAA5CC3;
    private const int ColorResolved = 0x43B581;
    private const int ColorInfo = 0x00A4DC;

    /// <summary>Builds the messages for new missing items, and resolved ones when given.</summary>
    /// <param name="changes">Series with something newly missing.</param>
    /// <param name="films">Film collections with something newly missing.</param>
    /// <param name="resolved">Series and collections where something turned up (empty to leave out).</param>
    /// <param name="publicServerUrl">Server address for links and posters, or empty.</param>
    /// <returns>Webhook payloads, in sending order. Empty when there's nothing to say.</returns>
    public static List<JsonObject> BuildChanges(IReadOnlyList<SeriesChange> changes, IReadOnlyList<CollectionChange> films, IReadOnlyList<ResolvedChange> resolved, string publicServerUrl)
    {
        var baseUrl = publicServerUrl.Trim().TrimEnd('/');
        var embeds = new List<JsonObject>();
        foreach (var change in changes.OrderBy(c => c.Series.Name, StringComparer.OrdinalIgnoreCase))
        {
            embeds.Add(Embed(
                Title(change.Series.Name, change.Series.Year),
                DescribeNew(change),
                ColorNew,
                baseUrl,
                change.Series.SeriesId,
                change.Series.Source == "Gaps" ? "Found from gaps in your episode numbers" : "Episode list from " + change.Series.Source));
        }

        foreach (var change in films.OrderBy(c => c.Collection.Name, StringComparer.OrdinalIgnoreCase))
        {
            var c = change.Collection;
            embeds.Add(Embed(
                "🎬 " + c.Name,
                DescribeFilms(change.Films),
                ColorFilm,
                baseUrl,
                c.BoxSetId,
                $"You have {c.OwnedCount} of {Plural(c.ReleasedCount, "released film", "released films")}",
                "https://www.themoviedb.org/collection/" + Uri.EscapeDataString(c.TmdbCollectionId)));
        }

        foreach (var r in resolved.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            embeds.Add(Embed("✅ " + r.Name, "Now in your library: " + Escape(string.Join(", ", r.Labels)), ColorResolved, baseUrl, r.ItemId, null));
        }

        if (embeds.Count == 0)
        {
            return [];
        }

        var parts = new List<string>();
        AddCount(parts, changes.Sum(c => c.Episodes.Count), "new missing episode", "new missing episodes");
        AddCount(parts, changes.Sum(c => c.Seasons.Count), "new missing season", "new missing seasons");
        AddCount(parts, films.Sum(c => c.Films.Count), "new missing film", "new missing films");
        var where = new List<string>();
        AddCount(where, changes.Count, "show", "shows");
        AddCount(where, films.Count, "collection", "collections");

        var headline = parts.Count > 0
            ? $"{(changes.Count > 0 ? "📺" : "🎬")} **{JoinAnd(parts)}** in {JoinAnd(where)}"
            : $"✅ **{Plural(resolved.Count, "item", "items")} caught up**";
        return Pack(headline, embeds);
    }

    /// <summary>
    /// Builds the message for the very first check: a summary instead of everything missing from the
    /// library, so turning the plugin on doesn't flood the channel.
    /// </summary>
    /// <param name="report">The first report.</param>
    /// <returns>Webhook payloads.</returns>
    public static List<JsonObject> BuildFirstScan(ScanReport report)
    {
        var found = new List<string>();
        AddCount(found, report.Series.Sum(s => s.Episodes.Count), "missing episode", "missing episodes");
        AddCount(found, report.Series.Sum(s => s.Seasons.Count), "missing season", "missing seasons");
        AddCount(found, report.Collections.Sum(c => c.Films.Count), "missing film", "missing films");
        var headline = "🏆 **Completionist is watching your library.** " +
            (found.Count == 0 ? "The first check found nothing missing. " : $"The first check found {JoinAnd(found)}. ") +
            "From now on you'll only hear about new ones. The full list is in Jellyfin under Dashboard → Completionist.";

        var embeds = new List<JsonObject>();
        if (report.Series.Count > 0)
        {
            var top = report.Series
                .OrderByDescending(s => s.Seasons.Sum(x => x.EpisodeCount) + s.Episodes.Count)
                .Take(15)
                .Select(s => $"**{Escape(s.Name)}**: {Summary(s)}")
                .ToList();
            var more = report.Series.Count - top.Count;
            var description = string.Join('\n', top) + (more > 0 ? $"\n…and {Plural(more, "more show", "more shows")}" : string.Empty);
            embeds.Add(Embed("Most incomplete shows", description, ColorInfo, string.Empty, null, null));
        }

        if (report.Collections.Count > 0)
        {
            var top = report.Collections
                .OrderByDescending(c => c.Films.Count)
                .Take(15)
                .Select(c => $"**{Escape(c.Name)}**: {c.OwnedCount} of {c.ReleasedCount}, missing {Plural(c.Films.Count, "film", "films")}")
                .ToList();
            var more = report.Collections.Count - top.Count;
            var description = string.Join('\n', top) + (more > 0 ? $"\n…and {Plural(more, "more collection", "more collections")}" : string.Empty);
            embeds.Add(Embed("Most incomplete film collections", description, ColorFilm, string.Empty, null, null));
        }

        return Pack(headline, embeds);
    }

    /// <summary>Builds a test message.</summary>
    /// <returns>Webhook payload.</returns>
    public static JsonObject BuildTest()
        => Message("✅ **Completionist** can post to this channel. You'll get a message here when an episode, season or film goes missing.", []);

    /// <summary>Short summary of what a series is missing, e.g. "Season 3, 4 episodes".</summary>
    /// <param name="series">The series.</param>
    /// <returns>The summary.</returns>
    internal static string Summary(SeriesReport series)
    {
        var parts = new List<string>();
        if (series.Seasons.Count > 0)
        {
            parts.Add((series.Seasons.Count == 1 ? "Season " : "Seasons ") + string.Join(", ", series.Seasons.Select(s => s.Season)));
        }

        if (series.Episodes.Count > 0)
        {
            parts.Add(Plural(series.Episodes.Count, "episode", "episodes"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>Formats an episode code, e.g. S02E05.</summary>
    /// <param name="season">Season.</param>
    /// <param name="episode">Episode.</param>
    /// <returns>The code.</returns>
    internal static string Code(int season, int episode)
        => string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", season, episode);

    private static string DescribeNew(SeriesChange change)
    {
        var lines = new List<string>();
        foreach (var s in change.Seasons.OrderBy(s => s.Season))
        {
            lines.Add($"📦 **{SeasonName(s.Season)}**, all {Plural(s.EpisodeCount, "episode", "episodes")}");
        }

        if (change.Episodes.Count <= ListEachEpisodeUpTo)
        {
            foreach (var e in change.Episodes.OrderBy(e => e.Season).ThenBy(e => e.Episode))
            {
                var line = new StringBuilder("`").Append(Code(e.Season, e.Episode)).Append('`');
                if (!string.IsNullOrWhiteSpace(e.Title))
                {
                    line.Append(' ').Append(Escape(e.Title));
                }

                if (e.AirDate is DateTime aired)
                {
                    line.Append(" · aired ").Append(aired.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
                }

                lines.Add(line.ToString());
            }
        }
        else
        {
            foreach (var season in change.Episodes.GroupBy(e => e.Season).OrderBy(g => g.Key))
            {
                var ranges = MissingFinder.ToRanges(season.Select(e => e.Episode))
                    .Select(r => r.First == r.Last
                        ? string.Format(CultureInfo.InvariantCulture, "E{0:00}", r.First)
                        : string.Format(CultureInfo.InvariantCulture, "E{0:00}–E{1:00}", r.First, r.Last));
                lines.Add($"**{SeasonName(season.Key)}**: {string.Join(", ", ranges)} ({Plural(season.Count(), "episode", "episodes")})");
            }
        }

        var text = string.Join('\n', lines);
        return text.Length <= MaxDescription ? text : text[..MaxDescription] + "\n…";
    }

    private static JsonObject Embed(string title, string description, int color, string baseUrl, Guid? itemId, string? footer, string? fallbackUrl = null)
    {
        var embed = new JsonObject
        {
            ["title"] = title.Length > 250 ? title[..250] + "…" : title,
            ["description"] = description,
            ["color"] = color,
        };
        if (baseUrl.Length > 0 && itemId is Guid g && g != Guid.Empty)
        {
            var id = g.ToString("N", CultureInfo.InvariantCulture);
            embed["url"] = $"{baseUrl}/web/#/details?id={id}";
            embed["thumbnail"] = new JsonObject { ["url"] = $"{baseUrl}/Items/{id}/Images/Primary?maxHeight=300" };
        }
        else if (fallbackUrl is not null)
        {
            embed["url"] = fallbackUrl;
        }

        if (footer is not null)
        {
            embed["footer"] = new JsonObject { ["text"] = footer };
        }

        return embed;
    }

    private static string DescribeFilms(IReadOnlyList<MissingFilm> films)
    {
        var lines = films
            .OrderBy(f => f.ReleaseDate)
            .Take(ListFilmsUpTo)
            .Select(f => f.ReleaseDate is DateTime d
                ? $"`{d.Year}` {Escape(f.Title)} · released {d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
                : Escape(f.Title))
            .ToList();
        if (films.Count > ListFilmsUpTo)
        {
            lines.Add($"…and {Plural(films.Count - ListFilmsUpTo, "more film", "more films")}");
        }

        var text = string.Join('\n', lines);
        return text.Length <= MaxDescription ? text : text[..MaxDescription] + "\n…";
    }

    private static void AddCount(List<string> parts, int count, string one, string many)
    {
        if (count > 0)
        {
            parts.Add(Plural(count, one, many));
        }
    }

    private static string JoinAnd(List<string> parts)
        => parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>Spreads embeds over as few messages as Discord's limits allow. The headline goes on the first.</summary>
    private static List<JsonObject> Pack(string headline, List<JsonObject> embeds)
    {
        var messages = new List<JsonObject>();
        var batch = new List<JsonObject>();
        var chars = headline.Length;
        foreach (var embed in embeds)
        {
            var size = Size(embed);
            if (batch.Count > 0 && (batch.Count == MaxEmbedsPerMessage || chars + size > MaxCharsPerMessage))
            {
                messages.Add(Message(messages.Count == 0 ? headline : null, batch));
                batch = [];
                chars = 0;
            }

            batch.Add(embed);
            chars += size;
        }

        messages.Add(Message(messages.Count == 0 ? headline : null, batch));
        return messages;
    }

    private static int Size(JsonObject embed)
        => (embed["title"]?.GetValue<string>().Length ?? 0)
            + (embed["description"]?.GetValue<string>().Length ?? 0)
            + ((embed["footer"] as JsonObject)?["text"]?.GetValue<string>().Length ?? 0);

    private static JsonObject Message(string? content, List<JsonObject> embeds)
    {
        var message = new JsonObject
        {
            ["username"] = "Jellyfin Completionist",

            // Show names could contain "@everyone"; never let a notification ping anyone.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        if (content is not null)
        {
            message["content"] = content;
        }

        if (embeds.Count > 0)
        {
            message["embeds"] = new JsonArray(embeds.Select(e => (JsonNode)e).ToArray());
        }

        return message;
    }

    private static string Title(string name, int? year) => year is int y ? $"{name} ({y})" : name;

    private static string SeasonName(int season) => season == 0 ? "Specials" : "Season " + season.ToString(CultureInfo.InvariantCulture);

    private static string Plural(int count, string one, string many)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '|' or '\\' or '>')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
