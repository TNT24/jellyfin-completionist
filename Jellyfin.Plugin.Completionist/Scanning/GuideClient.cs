using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.Completionist.Scanning;

/// <summary>The ids a series is known by.</summary>
/// <param name="Tmdb">TMDb id.</param>
/// <param name="Tvdb">TheTVDB id.</param>
/// <param name="Imdb">IMDb id (tt…).</param>
/// <param name="Ended">Whether the show has ended, so its guide can be cached for longer.</param>
public sealed record SeriesIds(string? Tmdb, string? Tvdb, string? Imdb, bool Ended);

/// <summary>An episode list and where it came from.</summary>
/// <param name="Source">"TMDb" or "TVmaze".</param>
/// <param name="Episodes">The episodes.</param>
public sealed record EpisodeGuide(string Source, IReadOnlyList<GuideEpisode> Episodes);

/// <summary>A film in a TMDb collection.</summary>
/// <param name="TmdbId">TMDb movie id.</param>
/// <param name="Title">Title.</param>
/// <param name="ReleaseDate">Release date (UTC), if known.</param>
public sealed record CollectionPart(int TmdbId, string Title, DateTime? ReleaseDate);

/// <summary>A TMDb collection (franchise) and its films.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="Parts">The films.</param>
public sealed record FilmCollection(string Name, IReadOnlyList<CollectionPart> Parts);

/// <summary>
/// Fetches episode lists from TMDb (when an API key is set) and TVmaze (free, no key), and film
/// collections from TMDb. Results are cached in memory, so checks after every library scan don't hit the APIs again.
/// </summary>
public sealed class GuideClient
{
    private static readonly TimeSpan ContinuingCacheTime = TimeSpan.FromHours(12);
    private static readonly TimeSpan EndedCacheTime = TimeSpan.FromDays(7);
    private static readonly TimeSpan CollectionCacheTime = TimeSpan.FromDays(3);

    // TVmaze allows 20 calls per 10 seconds; TMDb about 40 per second. One gap for both keeps it simple.
    private static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(550);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, (DateTime Fetched, EpisodeGuide? Guide)> _cache = new();
    private readonly ConcurrentDictionary<string, (DateTime Fetched, FilmCollection? Collection)> _collections = new();
    private readonly ConcurrentDictionary<string, string?> _tvmazeIds = new();
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public GuideClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Gets or sets a value indicating whether TMDb refused the API key during this run.</summary>
    public bool TmdbKeyRejected { get; set; }

    /// <summary>
    /// Gets a series' episode list: TMDb first if there's a key (it matches Jellyfin's default numbering),
    /// then TVmaze by TheTVDB or IMDb id.
    /// </summary>
    /// <param name="ids">The series' ids.</param>
    /// <param name="tmdbApiKey">TMDb API key or read access token; empty to skip TMDb.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The guide, or null if no source knows the show.</returns>
    public async Task<EpisodeGuide?> GetAsync(SeriesIds ids, string tmdbApiKey, CancellationToken cancellationToken)
    {
        var useTmdb = !string.IsNullOrWhiteSpace(tmdbApiKey) && !string.IsNullOrEmpty(ids.Tmdb) && !TmdbKeyRejected;
        var cacheKey = string.Join('|', ids.Tmdb, ids.Tvdb, ids.Imdb, useTmdb ? "tmdb" : "tvmaze");
        if (_cache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.Fetched < (ids.Ended ? EndedCacheTime : ContinuingCacheTime))
        {
            return cached.Guide;
        }

        EpisodeGuide? guide = null;
        if (useTmdb)
        {
            try
            {
                guide = await GetFromTmdbAsync(ids.Tmdb!, tmdbApiKey.Trim(), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A wrong key would fail every show the same way; fall back to TVmaze for the rest of the run.
                TmdbKeyRejected = true;
            }
        }

        if (guide is null)
        {
            var tvmazeId = await FindTvmazeIdAsync(ids, cancellationToken).ConfigureAwait(false);
            if (tvmazeId is not null)
            {
                guide = await GetFromTvmazeAsync(tvmazeId, cancellationToken).ConfigureAwait(false);
            }
        }

        _cache[cacheKey] = (DateTime.UtcNow, guide);
        return guide;
    }

    /// <summary>Gets a TMDb collection's films.</summary>
    /// <param name="collectionId">TMDb collection id.</param>
    /// <param name="tmdbApiKey">TMDb API key or read access token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The collection, or null if TMDb doesn't know it.</returns>
    public async Task<FilmCollection?> GetCollectionAsync(string collectionId, string tmdbApiKey, CancellationToken cancellationToken)
    {
        if (_collections.TryGetValue(collectionId, out var cached) && DateTime.UtcNow - cached.Fetched < CollectionCacheTime)
        {
            return cached.Collection;
        }

        var (url, token) = TmdbUrl($"collection/{Uri.EscapeDataString(collectionId)}", tmdbApiKey.Trim());
        var json = await GetJsonAsync(url, token, cancellationToken).ConfigureAwait(false);
        var collection = json is null ? null : ParseTmdbCollection(json);
        _collections[collectionId] = (DateTime.UtcNow, collection);
        return collection;
    }

    /// <summary>Forgets cached episode lists and collections, so the next check fetches everything again.</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _collections.Clear();
    }

    /// <summary>Parses a TMDb collection.</summary>
    /// <param name="json">The /collection/{id} response.</param>
    /// <returns>The collection.</returns>
    internal static FilmCollection ParseTmdbCollection(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var parts = new List<CollectionPart>();
        if (doc.RootElement.TryGetProperty("parts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray())
            {
                if (Int(p, "id") is int id)
                {
                    parts.Add(new CollectionPart(id, Str(p, "title") ?? Str(p, "name") ?? string.Empty, ParseDate(Str(p, "release_date"))));
                }
            }
        }

        return new FilmCollection(Str(doc.RootElement, "name") ?? string.Empty, parts);
    }

    /// <summary>Parses a TVmaze episode list. Specials have no number on TVmaze, so they're left out.</summary>
    /// <param name="json">The /shows/{id}/episodes response.</param>
    /// <returns>The episodes.</returns>
    internal static List<GuideEpisode> ParseTvmazeEpisodes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var episodes = new List<GuideEpisode>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (Int(e, "season") is not int season || Int(e, "number") is not int number)
            {
                continue;
            }

            var airDate = Str(e, "airstamp") is string stamp
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.UtcDateTime
                : ParseDate(Str(e, "airdate"));
            episodes.Add(new GuideEpisode(season, number, Str(e, "name"), airDate));
        }

        return episodes;
    }

    /// <summary>Parses the episodes of one TMDb season.</summary>
    /// <param name="json">The /tv/{id}/season/{n} response.</param>
    /// <returns>The episodes.</returns>
    internal static List<GuideEpisode> ParseTmdbSeason(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var episodes = new List<GuideEpisode>();
        if (!doc.RootElement.TryGetProperty("episodes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return episodes;
        }

        foreach (var e in list.EnumerateArray())
        {
            if (Int(e, "season_number") is int season && Int(e, "episode_number") is int number)
            {
                episodes.Add(new GuideEpisode(season, number, Str(e, "name"), ParseDate(Str(e, "air_date"))));
            }
        }

        return episodes;
    }

    private static DateTime? ParseDate(string? date)
        => DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string Url, string? Token) TmdbUrl(string path, string apiKey)
    {
        // A v4 read access token is a long JWT and goes in a header; a v3 key is short and goes in the query.
        var isToken = apiKey.Length > 40;
        var url = "https://api.themoviedb.org/3/" + path + (isToken ? string.Empty : "?api_key=" + Uri.EscapeDataString(apiKey));
        return (url, isToken ? apiKey : null);
    }

    private async Task<EpisodeGuide?> GetFromTmdbAsync(string tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        var (showUrl, token) = TmdbUrl($"tv/{Uri.EscapeDataString(tmdbId)}", apiKey);
        var show = await GetJsonAsync(showUrl, token, cancellationToken).ConfigureAwait(false);
        if (show is null)
        {
            return null;
        }

        List<int> seasons;
        using (var doc = JsonDocument.Parse(show))
        {
            seasons = doc.RootElement.TryGetProperty("seasons", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(s => Int(s, "season_number")).OfType<int>().ToList()
                : [];
        }

        var episodes = new List<GuideEpisode>();
        foreach (var season in seasons)
        {
            var (seasonUrl, _) = TmdbUrl($"tv/{Uri.EscapeDataString(tmdbId)}/season/{season}", apiKey);
            var json = await GetJsonAsync(seasonUrl, token, cancellationToken).ConfigureAwait(false);
            if (json is not null)
            {
                episodes.AddRange(ParseTmdbSeason(json));
            }
        }

        return new EpisodeGuide("TMDb", episodes);
    }

    private async Task<string?> FindTvmazeIdAsync(SeriesIds ids, CancellationToken cancellationToken)
    {
        var lookups = new List<string>();
        if (!string.IsNullOrEmpty(ids.Tvdb))
        {
            lookups.Add("thetvdb=" + Uri.EscapeDataString(ids.Tvdb));
        }

        if (!string.IsNullOrEmpty(ids.Imdb))
        {
            lookups.Add("imdb=" + Uri.EscapeDataString(ids.Imdb));
        }

        foreach (var lookup in lookups)
        {
            if (!_tvmazeIds.TryGetValue(lookup, out var id))
            {
                // The lookup answers with a redirect to the show, which the HTTP client follows.
                var json = await GetJsonAsync("https://api.tvmaze.com/lookup/shows?" + lookup, null, cancellationToken).ConfigureAwait(false);
                if (json is not null)
                {
                    using var doc = JsonDocument.Parse(json);
                    id = Int(doc.RootElement, "id")?.ToString(CultureInfo.InvariantCulture);
                }

                _tvmazeIds[lookup] = id;
            }

            if (id is not null)
            {
                return id;
            }
        }

        return null;
    }

    private async Task<EpisodeGuide?> GetFromTvmazeAsync(string tvmazeId, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync($"https://api.tvmaze.com/shows/{tvmazeId}/episodes?specials=1", null, cancellationToken).ConfigureAwait(false);
        return json is null ? null : new EpisodeGuide("TVmaze", ParseTvmazeEpisodes(json));
    }

    /// <summary>Sends a GET, spaced out and retried on rate limits. Returns null for 404.</summary>
    private async Task<string?> GetJsonAsync(string url, string? bearerToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        for (var attempt = 0; ; attempt++)
        {
            await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _lastRequest + RequestGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                _lastRequest = DateTime.UtcNow;
            }
            finally
            {
                _throttle.Release();
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearerToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
