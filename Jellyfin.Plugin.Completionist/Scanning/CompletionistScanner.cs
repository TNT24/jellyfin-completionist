using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Completionist.Configuration;
using Jellyfin.Plugin.Completionist.Notifications;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Completionist.Scanning;

/// <summary>
/// Checks every series against its episode guide and every film collection against TMDb, keeps the report,
/// and tells Discord what's new since last time.
/// </summary>
public sealed class CompletionistScanner
{
    private readonly ILibraryManager _libraryManager;
    private readonly GuideClient _guides;
    private readonly ReportStore _store;
    private readonly DiscordNotifier _discord;
    private readonly ActivityLog _activity;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CompletionistScanner"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="guides">Guide client.</param>
    /// <param name="store">Report store.</param>
    /// <param name="discord">Discord notifier.</param>
    /// <param name="activity">Activity log.</param>
    public CompletionistScanner(
        ILibraryManager libraryManager,
        GuideClient guides,
        ReportStore store,
        DiscordNotifier discord,
        ActivityLog activity)
    {
        _libraryManager = libraryManager;
        _guides = guides;
        _store = store;
        _discord = discord;
        _activity = activity;
    }

    /// <summary>Gets a value indicating whether a check is in progress.</summary>
    public bool IsRunning => _runLock.CurrentCount == 0;

    /// <summary>Checks every series and film collection, saves the report and sends notifications.</summary>
    /// <param name="progress">Progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="afterLibraryScan">Whether a library scan started this check. Those don't post an "all clear".</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken, bool afterLibraryScan = false)
    {
        // A scheduled run and a post-scan run shouldn't overlap and notify twice.
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _activity.Info("A check is already running; skipping this one");
            return;
        }

        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var now = DateTime.UtcNow;
            var previous = _store.Get();
            var hasTmdbKey = !string.IsNullOrWhiteSpace(config.TmdbApiKey);
            _guides.TmdbKeyRejected = false;

            var shows = config.CheckShows ? LoadShows(config) : null;
            var films = config.CheckFilmCollections && hasTmdbKey ? LoadFilms(config) : null;
            if (config.CheckFilmCollections && !hasTmdbKey)
            {
                _activity.Info("Film collections are skipped: they need a TMDb API key (see the settings)");
            }

            var report = new ScanReport
            {
                ScannedAt = now,
                FirstScannedAt = previous is null || previous.FirstScannedAt == default ? now : previous.FirstScannedAt,
                SeriesChecked = shows?.Series.Count ?? 0,
                CollectionsChecked = films?.Collections.Count ?? 0,
            };

            // One progress bar over both parts: one unit per show and per collection.
            var total = Math.Max(1, report.SeriesChecked + report.CollectionsChecked);
            var showsFresh = shows is null
                ? []
                : await CheckShowsAsync(shows, config, previous, report, new Progress<int>(i => progress.Report(100.0 * i / total)), cancellationToken).ConfigureAwait(false);
            var filmsFresh = films is null
                ? []
                : await CheckFilmsAsync(films, config, previous, report, new Progress<int>(i => progress.Report(100.0 * (report.SeriesChecked + i) / total)), cancellationToken).ConfigureAwait(false);

            report.Series.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            report.Collections.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            var changes = previous is null ? [] : FindNew(report, now);
            var filmChanges = previous is null ? [] : FindNewFilms(report, now);
            var resolvedShows = previous is null ? [] : FindResolved(previous, report, showsFresh);
            var resolvedFilms = previous is null ? [] : FindResolvedFilms(previous, report, filmsFresh);
            var resolved = resolvedShows.Concat(resolvedFilms).ToList();
            report.Filled = KeepFilled(previous?.Filled ?? [], resolvedShows, resolvedFilms, now);

            _store.Set(report);
            progress.Report(100);

            var summary = new List<string>();
            if (shows is not null)
            {
                summary.Add($"{report.Series.Sum(s => s.Episodes.Count)} missing episodes and {report.Series.Sum(s => s.Seasons.Count)} missing seasons in {report.Series.Count} shows");
            }

            if (films is not null)
            {
                summary.Add($"{report.Collections.Sum(c => c.Films.Count)} missing films in {report.Collections.Count} collections");
            }

            _activity.Info(
                "Done: " + (summary.Count == 0 ? "nothing to check" : string.Join("; ", summary)) +
                (previous is null ? string.Empty : $". {changes.Sum(c => c.Episodes.Count + c.Seasons.Count) + filmChanges.Sum(c => c.Films.Count)} new, {resolved.Sum(r => r.Labels.Count)} found since last check"));

            await NotifyAsync(config, previous is null, !afterLibraryScan, report, changes, filmChanges, resolved, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Starts a check without waiting for it.</summary>
    /// <param name="afterLibraryScan">Whether a library scan started this check.</param>
    public void RunInBackground(bool afterLibraryScan = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(new Progress<double>(), CancellationToken.None, afterLibraryScan).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _activity.Error("Check failed", ex);
            }
        });
    }

    /// <summary>
    /// Copies "first seen" times from the last report, so only things that weren't missing before count as new.
    /// An episode in a season that was missing as a whole isn't new either.
    /// </summary>
    /// <param name="entry">This check's result for the series.</param>
    /// <param name="previous">Last check's result, if the series had anything missing then.</param>
    /// <param name="now">This check's time.</param>
    internal static void StampFirstSeen(SeriesReport entry, SeriesReport? previous, DateTime now)
    {
        var seasons = previous?.Seasons.ToDictionary(s => s.Season, s => s.FirstSeen) ?? [];
        var eps = previous?.Episodes.ToDictionary(e => (e.Season, e.Episode), e => e.FirstSeen) ?? [];
        foreach (var s in entry.Seasons)
        {
            s.FirstSeen = seasons.TryGetValue(s.Season, out var seen) ? seen : now;
        }

        foreach (var e in entry.Episodes)
        {
            e.FirstSeen = eps.TryGetValue((e.Season, e.Episode), out var seen) ? seen
                : seasons.TryGetValue(e.Season, out var seasonSeen) ? seasonSeen
                : now;
        }
    }

    /// <summary>Copies "first seen" times for films from the last report.</summary>
    /// <param name="entry">This check's result for the collection.</param>
    /// <param name="previous">Last check's result, if the collection had anything missing then.</param>
    /// <param name="now">This check's time.</param>
    internal static void StampFirstSeen(CollectionReport entry, CollectionReport? previous, DateTime now)
    {
        var seen = previous?.Films.ToDictionary(f => f.TmdbId, f => f.FirstSeen) ?? [];
        foreach (var f in entry.Films)
        {
            f.FirstSeen = seen.TryGetValue(f.TmdbId, out var t) ? t : now;
        }
    }

    /// <summary>Picks out everything first seen in this check.</summary>
    /// <param name="report">This check's report.</param>
    /// <param name="now">This check's time.</param>
    /// <returns>Series with something new.</returns>
    internal static List<SeriesChange> FindNew(ScanReport report, DateTime now)
        => report.Series
            .Select(s => new SeriesChange(s, s.Seasons.Where(x => x.FirstSeen == now).ToList(), s.Episodes.Where(x => x.FirstSeen == now).ToList()))
            .Where(c => c.Seasons.Count > 0 || c.Episodes.Count > 0)
            .ToList();

    /// <summary>Picks out films first seen missing in this check.</summary>
    /// <param name="report">This check's report.</param>
    /// <param name="now">This check's time.</param>
    /// <returns>Collections with something new.</returns>
    internal static List<CollectionChange> FindNewFilms(ScanReport report, DateTime now)
        => report.Collections
            .Select(c => new CollectionChange(c, c.Films.Where(f => f.FirstSeen == now).ToList()))
            .Where(c => c.Films.Count > 0)
            .ToList();

    /// <summary>Adds what turned up in this check to the history, dropping entries older than the weekly summary needs.</summary>
    /// <param name="history">The history so far.</param>
    /// <param name="shows">Series where something turned up.</param>
    /// <param name="films">Collections where a film turned up.</param>
    /// <param name="now">This check's time.</param>
    /// <returns>The new history.</returns>
    internal static List<FilledEntry> KeepFilled(IEnumerable<FilledEntry> history, IEnumerable<ResolvedChange> shows, IEnumerable<ResolvedChange> films, DateTime now)
    {
        // A little over a week, so a summary that runs late still sees the whole week.
        var kept = history.Where(f => f.Time > now.AddDays(-8)).ToList();
        foreach (var s in shows)
        {
            var seasons = s.Labels.Count(l => l.StartsWith("Season ", StringComparison.Ordinal));
            kept.Add(new FilledEntry { Time = now, ItemId = s.ItemId, Name = s.Name, Seasons = seasons, Items = s.Labels.Count - seasons });
        }

        foreach (var f in films)
        {
            kept.Add(new FilledEntry { Time = now, ItemId = f.ItemId, Name = f.Name, IsCollection = true, Items = f.Labels.Count });
        }

        return kept;
    }

    /// <summary>Finds what was missing last time and isn't now, for series that were really checked this time.</summary>
    /// <param name="previous">Last check's report.</param>
    /// <param name="report">This check's report.</param>
    /// <param name="checkedFresh">Series checked against a guide this time (not carried over or skipped).</param>
    /// <returns>Series where something turned up.</returns>
    internal static List<ResolvedChange> FindResolved(ScanReport previous, ScanReport report, HashSet<Guid> checkedFresh)
    {
        var current = report.Series.ToDictionary(s => s.SeriesId);
        var resolved = new List<ResolvedChange>();
        foreach (var old in previous.Series.Where(s => checkedFresh.Contains(s.SeriesId)))
        {
            var now = current.GetValueOrDefault(old.SeriesId);
            var labels = new List<string>();
            foreach (var s in old.Seasons.Where(s => now is null || !now.Seasons.Any(x => x.Season == s.Season)))
            {
                // A season that went from wholly to partly missing isn't caught up; only count it when nothing in it is left.
                if (now is null || !now.Episodes.Any(e => e.Season == s.Season))
                {
                    labels.Add("Season " + s.Season.ToString(CultureInfo.InvariantCulture));
                }
            }

            var episodes = old.Episodes
                .Where(e => now is null || !now.Episodes.Any(x => x.Season == e.Season && x.Episode == e.Episode))
                .ToList();
            labels.AddRange(episodes.Select(e => DiscordMessageBuilder.Code(e.Season, e.Episode)));
            if (labels.Count > 0)
            {
                resolved.Add(new ResolvedChange(old.SeriesId, old.Name, labels));
            }
        }

        return resolved;
    }

    /// <summary>Finds films that were missing last time and aren't now, for collections checked this time.</summary>
    /// <param name="previous">Last check's report.</param>
    /// <param name="report">This check's report.</param>
    /// <param name="checkedFresh">TMDb ids of collections checked this time.</param>
    /// <returns>Collections where a film turned up.</returns>
    internal static List<ResolvedChange> FindResolvedFilms(ScanReport previous, ScanReport report, HashSet<string> checkedFresh)
    {
        var current = report.Collections.ToDictionary(c => c.TmdbCollectionId);
        var resolved = new List<ResolvedChange>();
        foreach (var old in previous.Collections.Where(c => checkedFresh.Contains(c.TmdbCollectionId)))
        {
            var now = current.GetValueOrDefault(old.TmdbCollectionId);
            var found = old.Films
                .Where(f => now is null || !now.Films.Any(x => x.TmdbId == f.TmdbId))
                .Select(f => f.Title)
                .ToList();
            if (found.Count > 0)
            {
                resolved.Add(new ResolvedChange(now?.BoxSetId ?? old.BoxSetId, old.Name, found));
            }
        }

        return resolved;
    }

    private async Task<HashSet<Guid>> CheckShowsAsync(ShowData shows, PluginConfiguration config, ScanReport? previous, ScanReport report, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var options = new FinderOptions(config.IncludeSpecials, config.ReportWholeSeasons, report.ScannedAt.AddDays(-Math.Max(0, config.GraceDays)));
        var previousById = previous?.Series.ToDictionary(s => s.SeriesId) ?? [];
        var checkedFresh = new HashSet<Guid>();
        _activity.Info($"Checking {shows.Series.Count} shows" + (shows.Ignored > 0 ? $" ({shows.Ignored} ignored)" : string.Empty));

        for (var i = 0; i < shows.Series.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(i);

            var show = shows.Series[i];
            var local = shows.Episodes.GetValueOrDefault(show.Id) ?? [];
            if (local.Count == 0)
            {
                continue;
            }

            EpisodeGuide? guide;
            try
            {
                guide = await _guides.GetAsync(Ids(show), config.TmdbApiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep last time's result, so a network blip doesn't make everything look found and then new again.
                _activity.Warning($"{show.Name}: couldn't load the episode list ({ex.Message})");
                report.Problems.Add(Problem(show, "Couldn't reach the episode guide: " + ex.Message));
                if (previousById.TryGetValue(show.Id, out var old))
                {
                    old.Note = "From an earlier check; the episode guide couldn't be reached this time.";
                    report.Series.Add(old);
                }

                continue;
            }

            var entry = Check(show, local, guide, options, config, report);
            checkedFresh.Add(show.Id);
            if (entry is not null)
            {
                StampFirstSeen(entry, previousById.GetValueOrDefault(show.Id), report.ScannedAt);
                report.Series.Add(entry);
            }
        }

        if (_guides.TmdbKeyRejected)
        {
            _activity.Warning("TMDb rejected the API key, so TVmaze was used instead. Check the key in the settings.");
        }

        return checkedFresh;
    }

    private async Task<HashSet<string>> CheckFilmsAsync(FilmData films, PluginConfiguration config, ScanReport? previous, ScanReport report, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var releasedBefore = report.ScannedAt.AddDays(-Math.Max(0, config.FilmGraceDays));
        var ignoredFilms = config.IgnoredFilms.Select(f => int.TryParse(f.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).ToHashSet();
        var previousById = previous?.Collections.ToDictionary(c => c.TmdbCollectionId) ?? [];
        var checkedFresh = new HashSet<string>();
        _activity.Info($"Checking {films.Collections.Count} film collections ({films.Owned.Count} films with a TMDb id)");

        for (var i = 0; i < films.Collections.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(i);

            var collectionId = films.Collections[i];
            FilmCollection? collection;
            try
            {
                collection = await _guides.GetCollectionAsync(collectionId, config.TmdbApiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var keyRejected = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };
                _activity.Warning(keyRejected
                    ? "TMDb rejected the API key; film collections weren't checked. Check the key in the settings."
                    : $"Film collection {collectionId}: couldn't load it from TMDb ({ex.Message})");

                // Keep last time's results for this collection, or for all remaining ones if the key is bad.
                foreach (var id in keyRejected ? films.Collections.Skip(i) : [collectionId])
                {
                    if (previousById.TryGetValue(id, out var old))
                    {
                        old.Note = "From an earlier check; TMDb couldn't be reached this time.";
                        report.Collections.Add(old);
                    }
                }

                if (keyRejected)
                {
                    break;
                }

                continue;
            }

            checkedFresh.Add(collectionId);
            if (collection is null)
            {
                continue;
            }

            var released = collection.Parts.Where(p => p.ReleaseDate is DateTime d && d < releasedBefore).DistinctBy(p => p.TmdbId).ToList();
            var owned = released.Count(p => films.Owned.Contains(p.TmdbId));
            var missing = MissingFinder.FindMissingFilms(collection.Parts, films.Owned, ignoredFilms, releasedBefore);

            // A Jellyfin collection whose films are all in other libraries (or gone) isn't one you're collecting.
            if (owned == 0 || missing.Count == 0)
            {
                continue;
            }

            var entry = new CollectionReport
            {
                TmdbCollectionId = collectionId,
                Name = string.IsNullOrEmpty(collection.Name) ? "Collection " + collectionId : collection.Name,
                BoxSetId = films.BoxSets.TryGetValue(collectionId, out var boxSet) ? boxSet : null,
                OwnedCount = owned,
                ReleasedCount = released.Count,
                Films = missing.Select(p => new MissingFilm { TmdbId = p.TmdbId, Title = p.Title, ReleaseDate = p.ReleaseDate }).ToList(),
            };
            StampFirstSeen(entry, previousById.GetValueOrDefault(collectionId), report.ScannedAt);
            report.Collections.Add(entry);
        }

        return checkedFresh;
    }

    private static SeriesReport? Check(Series show, List<LocalEpisode> local, EpisodeGuide? guide, FinderOptions options, PluginConfiguration config, ScanReport report)
    {
        if (guide is not null)
        {
            var found = MissingFinder.Compare(local, guide.Episodes, options);
            if (!found.NumberingMismatch)
            {
                if (found.MissingSeasons.Count == 0 && found.MissingEpisodes.Count == 0)
                {
                    return null;
                }

                var entry = NewEntry(show, guide.Source);
                entry.Seasons = found.MissingSeasons.Select(s => new MissingSeason { Season = s.Season, EpisodeCount = s.AiredCount }).ToList();
                entry.Episodes = found.MissingEpisodes.Select(ToMissing).ToList();
                return entry;
            }

            report.Problems.Add(Problem(show, $"Episode numbers don't line up with {guide.Source} (absolute or by-year numbering?)" + (config.CheckGapsWithoutGuide ? "; checked for gaps instead" : string.Empty)));
        }
        else
        {
            report.Problems.Add(Problem(show, (HasAnyId(show) ? "No episode guide knows this show" : "No TMDb, TheTVDB or IMDb id; refresh its metadata") + (config.CheckGapsWithoutGuide ? "; checked for gaps instead" : string.Empty)));
        }

        if (!config.CheckGapsWithoutGuide)
        {
            return null;
        }

        var gaps = MissingFinder.FindGaps(local, config.IncludeSpecials);
        if (gaps.Count == 0)
        {
            return null;
        }

        var gapEntry = NewEntry(show, "Gaps");
        gapEntry.Episodes = gaps.Select(ToMissing).ToList();
        return gapEntry;
    }

    private static SeriesReport NewEntry(Series show, string source)
        => new() { SeriesId = show.Id, Name = show.Name, Year = show.ProductionYear, Source = source };

    private static MissingEpisode ToMissing(GuideEpisode e)
        => new() { Season = e.Season, Episode = e.Episode, Title = e.Title, AirDate = e.AirDate };

    private static SeriesProblem Problem(Series show, string reason)
        => new() { SeriesId = show.Id, Name = show.Name, Reason = reason };

    private static SeriesIds Ids(Series show)
        => new(
            show.GetProviderId(MetadataProvider.Tmdb),
            show.GetProviderId(MetadataProvider.Tvdb),
            show.GetProviderId(MetadataProvider.Imdb),
            show.Status == SeriesStatus.Ended);

    private static bool HasAnyId(Series show)
    {
        var ids = Ids(show);
        return !string.IsNullOrEmpty(ids.Tmdb) || !string.IsNullOrEmpty(ids.Tvdb) || !string.IsNullOrEmpty(ids.Imdb);
    }

    private static HashSet<Guid> ParseIds(IEnumerable<string> ids)
        => ids.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();

    private List<BaseItem> GetItems(PluginConfiguration config, BaseItemKind kind, DtoOptions? dtoOptions = null)
    {
        var libraries = ParseIds(config.LibraryIds);
        var parents = libraries.Count == 0 ? [Guid.Empty] : libraries.ToList();
        return parents
            .SelectMany(parent =>
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = [kind],
                    IsVirtualItem = false,
                    Recursive = true,
                    ParentId = parent,
                };
                if (dtoOptions is not null)
                {
                    query.DtoOptions = dtoOptions;
                }

                return _libraryManager.GetItemList(query);
            })
            .DistinctBy(i => i.Id)
            .ToList();
    }

    private ShowData LoadShows(PluginConfiguration config)
    {
        var all = GetItems(config, BaseItemKind.Series).OfType<Series>().ToList();
        var ignored = ParseIds(config.IgnoredSeriesIds);
        var series = all.Where(s => !ignored.Contains(s.Id)).ToList();
        var ids = series.Select(s => s.Id).ToHashSet();

        // Read-only, so only the basic columns are loaded; no images, user data or extra fields.
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
            })
            .OfType<Episode>()
            .Where(e => ids.Contains(e.SeriesId) && e.ParentIndexNumber is not null && e.IndexNumber is not null)
            .GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(e => new LocalEpisode(e.ParentIndexNumber!.Value, e.IndexNumber!.Value, e.IndexNumberEnd)).ToList());

        return new ShowData(series, episodes, all.Count - series.Count);
    }

    private FilmData LoadFilms(PluginConfiguration config)
    {
        // Read-only, so only provider ids are loaded on top of the basic columns.
        var movies = GetItems(config, BaseItemKind.Movie, new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false });
        var owned = movies
            .Select(m => int.TryParse(m.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

        // Every franchise you own a film of. Jellyfin's TMDb provider sets the TmdbCollection id on each movie.
        var collections = movies
            .Select(m => m.GetProviderId(MetadataProvider.TmdbCollection))
            .OfType<string>()
            .Where(id => id.Length > 0)
            .ToHashSet();

        // Jellyfin collections carry the TMDb collection id too (as Tmdb, or TmdbCollection from Franchise
        // Collections), which links reports to them and covers films whose own id is missing.
        var boxSets = new Dictionary<string, Guid>();
        foreach (var boxSet in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true }).OfType<BoxSet>())
        {
            var id = boxSet.GetProviderId(MetadataProvider.TmdbCollection) ?? boxSet.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(id))
            {
                boxSets.TryAdd(id, boxSet.Id);
                collections.Add(id);
            }
        }

        var ignored = config.IgnoredCollections.Select(c => c.Id).ToHashSet();
        return new FilmData(collections.Where(id => !ignored.Contains(id)).Order(StringComparer.Ordinal).ToList(), owned, boxSets);
    }

    private async Task NotifyAsync(PluginConfiguration config, bool firstScan, bool allowAllClear, ScanReport report, List<SeriesChange> changes, List<CollectionChange> filmChanges, List<ResolvedChange> resolved, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
        {
            return;
        }

        var messages = firstScan
            ? DiscordMessageBuilder.BuildFirstScan(report)
            : DiscordMessageBuilder.BuildChanges(changes, filmChanges, config.NotifyResolved ? resolved : [], config.PublicServerUrl);
        var nothingNew = messages.Count == 0;
        if (nothingNew)
        {
            if (!config.NotifyNothingNew || !allowAllClear)
            {
                return;
            }

            messages = [DiscordMessageBuilder.BuildNothingNew(report)];
        }

        try
        {
            await _discord.SendAsync(config.DiscordWebhookUrl, messages, cancellationToken).ConfigureAwait(false);
            var told = changes.Count + filmChanges.Count + (config.NotifyResolved ? resolved.Count : 0);
            _activity.Info(
                firstScan ? "Sent the first-check summary to Discord"
                : nothingNew ? "Told Discord nothing new is missing"
                : $"Told Discord about {told} {(told == 1 ? "show or collection" : "shows and collections")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activity.Error("Couldn't post to Discord", ex);
        }
    }

    private sealed record ShowData(List<Series> Series, Dictionary<Guid, List<LocalEpisode>> Episodes, int Ignored);

    private sealed record FilmData(List<string> Collections, HashSet<int> Owned, Dictionary<string, Guid> BoxSets);
}
