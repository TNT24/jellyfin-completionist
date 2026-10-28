using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Completionist.Scanning;

/// <summary>A season that has aired but has no episodes in the library.</summary>
public sealed class MissingSeason
{
    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets how many of its episodes have aired.</summary>
    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets when it was first reported missing (UTC).</summary>
    public DateTime FirstSeen { get; set; }
}

/// <summary>A single missing episode.</summary>
public sealed class MissingEpisode
{
    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets the title, if the guide has one.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the air date (UTC), if the guide has one.</summary>
    public DateTime? AirDate { get; set; }

    /// <summary>Gets or sets when it was first reported missing (UTC).</summary>
    public DateTime FirstSeen { get; set; }
}

/// <summary>Everything missing from one series.</summary>
public sealed class SeriesReport
{
    /// <summary>Gets or sets the Jellyfin series id.</summary>
    public Guid SeriesId { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the first-aired year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets where the episode list came from: "TMDb", "TVmaze" or "Gaps" (numbering holes only).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets whole seasons that are missing.</summary>
    public List<MissingSeason> Seasons { get; set; } = [];

    /// <summary>Gets or sets single missing episodes.</summary>
    public List<MissingEpisode> Episodes { get; set; } = [];

    /// <summary>Gets or sets a note when this result was carried over from an earlier scan.</summary>
    public string? Note { get; set; }
}

/// <summary>A released film missing from a collection.</summary>
public sealed class MissingFilm
{
    /// <summary>Gets or sets the TMDb movie id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release date (UTC).</summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>Gets or sets when it was first reported missing (UTC).</summary>
    public DateTime FirstSeen { get; set; }
}

/// <summary>Everything missing from one film collection (TMDb franchise).</summary>
public sealed class CollectionReport
{
    /// <summary>Gets or sets the TMDb collection id.</summary>
    public string TmdbCollectionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the matching Jellyfin collection, if there is one.</summary>
    public Guid? BoxSetId { get; set; }

    /// <summary>Gets or sets how many of its released films are in the library.</summary>
    public int OwnedCount { get; set; }

    /// <summary>Gets or sets how many of its films have been released.</summary>
    public int ReleasedCount { get; set; }

    /// <summary>Gets or sets the missing films.</summary>
    public List<MissingFilm> Films { get; set; } = [];

    /// <summary>Gets or sets a note when this result was carried over from an earlier check.</summary>
    public string? Note { get; set; }
}

/// <summary>A series that couldn't be checked.</summary>
public sealed class SeriesProblem
{
    /// <summary>Gets or sets the Jellyfin series id.</summary>
    public Guid SeriesId { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets why it couldn't be checked.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>The result of the last full check.</summary>
public sealed class ScanReport
{
    /// <summary>Gets or sets when the check finished (UTC).</summary>
    public DateTime ScannedAt { get; set; }

    /// <summary>
    /// Gets or sets when the very first check ran (UTC). Everything found then was already missing,
    /// so only things first seen after it count as new.
    /// </summary>
    public DateTime FirstScannedAt { get; set; }

    /// <summary>Gets or sets how many series were checked.</summary>
    public int SeriesChecked { get; set; }

    /// <summary>Gets or sets the series with something missing.</summary>
    public List<SeriesReport> Series { get; set; } = [];

    /// <summary>Gets or sets the series that couldn't be checked.</summary>
    public List<SeriesProblem> Problems { get; set; } = [];

    /// <summary>Gets or sets how many film collections were checked (0 when films are off).</summary>
    public int CollectionsChecked { get; set; }

    /// <summary>Gets or sets the film collections with something missing.</summary>
    public List<CollectionReport> Collections { get; set; } = [];

    /// <summary>Gets or sets what turned up in recent checks, kept for the weekly summary.</summary>
    public List<FilledEntry> Filled { get; set; } = [];
}

/// <summary>Missing items that turned up in one series or collection during one check.</summary>
public sealed class FilledEntry
{
    /// <summary>Gets or sets when the check found them (UTC).</summary>
    public DateTime Time { get; set; }

    /// <summary>Gets or sets the Jellyfin series or collection, if there is one.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the series or collection name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is a film collection rather than a series.</summary>
    public bool IsCollection { get; set; }

    /// <summary>Gets or sets how many whole seasons turned up.</summary>
    public int Seasons { get; set; }

    /// <summary>Gets or sets how many episodes (or, for a collection, films) turned up.</summary>
    public int Items { get; set; }
}

/// <summary>
/// Keeps the last report, so the settings page can show it and the next check can tell what's new.
/// Persisted as JSON in the plugin's data folder.
/// </summary>
public sealed class ReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<ReportStore> _logger;
    private ScanReport? _report;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportStore"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public ReportStore(IApplicationPaths paths, ILogger<ReportStore> logger)
    {
        _path = Path.Combine(paths.PluginConfigurationsPath, "Completionist", "report.json");
        _logger = logger;
    }

    /// <summary>Gets the last report, or null before the first check.</summary>
    /// <returns>The report.</returns>
    public ScanReport? Get()
    {
        lock (_lock)
        {
            return Load();
        }
    }

    /// <summary>Replaces the report and writes it to disk.</summary>
    /// <param name="report">The new report.</param>
    public void Set(ScanReport report)
    {
        lock (_lock)
        {
            _report = report;
            _loaded = true;
            Save();
        }
    }

    /// <summary>Removes one series from the report (after it's ignored) and writes it to disk.</summary>
    /// <param name="seriesId">The series.</param>
    public void RemoveSeries(Guid seriesId)
    {
        lock (_lock)
        {
            var report = Load();
            if (report is null)
            {
                return;
            }

            report.Series.RemoveAll(s => s.SeriesId == seriesId);
            report.Problems.RemoveAll(s => s.SeriesId == seriesId);
            Save();
        }
    }

    /// <summary>Removes a film collection, or one film of it, from the report (after it's ignored) and writes it to disk.</summary>
    /// <param name="collectionId">TMDb collection id.</param>
    /// <param name="filmId">TMDb movie id, or null for the whole collection.</param>
    public void RemoveFilms(string collectionId, int? filmId)
    {
        lock (_lock)
        {
            var report = Load();
            var collection = report?.Collections.FirstOrDefault(c => c.TmdbCollectionId == collectionId);
            if (collection is null)
            {
                return;
            }

            if (filmId is int id)
            {
                collection.Films.RemoveAll(f => f.TmdbId == id);
            }

            if (filmId is null || collection.Films.Count == 0)
            {
                report!.Collections.Remove(collection);
            }

            Save();
        }
    }

    /// <summary>Forgets the report, so the next check starts fresh and counts as a first check.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _report = null;
            _loaded = true;
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete missing episodes report at {Path}", _path);
            }
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_report, JsonOptions));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save missing episodes report to {Path}", _path);
        }
    }

    private ScanReport? Load()
    {
        if (_loaded)
        {
            return _report;
        }

        _loaded = true;
        try
        {
            if (File.Exists(_path))
            {
                _report = JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(_path), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable missing episodes report at {Path}", _path);
        }

        return _report;
    }
}
