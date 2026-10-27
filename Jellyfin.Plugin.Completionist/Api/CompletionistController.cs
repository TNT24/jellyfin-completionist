using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Completionist.Configuration;
using Jellyfin.Plugin.Completionist.Notifications;
using Jellyfin.Plugin.Completionist.Scanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Completionist.Api;

/// <summary>
/// Endpoints used by the plugin's page.
/// </summary>
[ApiController]
[Route("Completionist")]
[Authorize(Policy = "RequiresElevation")]
public class CompletionistController : ControllerBase
{
    private readonly ReportStore _store;
    private readonly CompletionistScanner _scanner;
    private readonly GuideClient _guides;
    private readonly DiscordNotifier _discord;
    private readonly ActivityLog _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompletionistController"/> class.
    /// </summary>
    /// <param name="store">Report store.</param>
    /// <param name="scanner">Scanner.</param>
    /// <param name="guides">Episode guide client.</param>
    /// <param name="discord">Discord notifier.</param>
    /// <param name="activity">Activity log.</param>
    public CompletionistController(ReportStore store, CompletionistScanner scanner, GuideClient guides, DiscordNotifier discord, ActivityLog activity)
    {
        _store = store;
        _scanner = scanner;
        _guides = guides;
        _discord = discord;
        _activity = activity;
    }

    /// <summary>Gets the last report and whether a check is running.</summary>
    /// <returns>The report.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetReport()
    {
        return new { Report = _store.Get(), _scanner.IsRunning };
    }

    /// <summary>Gets the plugin's activity log, newest first.</summary>
    /// <returns>Log entries.</returns>
    [HttpGet("Log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetLog()
    {
        return _activity.Recent()
            .Select(e => new { e.Time, Level = e.Level.ToString(), e.Message })
            .ToList();
    }

    /// <summary>Clears the activity log.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Log/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearLog()
    {
        _activity.Clear();
        return NoContent();
    }

    /// <summary>Starts a check in the background.</summary>
    /// <param name="refresh">Also forget cached episode lists, so every show is looked up again.</param>
    /// <returns>Whether it started, and a message for the user.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Run([FromQuery] bool refresh = false)
    {
        if (_scanner.IsRunning)
        {
            return new { Started = false, Message = "A check is already running. It'll show up here when it's done." };
        }

        if (refresh)
        {
            _guides.ClearCache();
        }

        _scanner.RunInBackground();
        return new { Started = true, Message = "Check started. The first one looks up every show and film collection and can take a few minutes." };
    }

    /// <summary>Sends a test message to the Discord webhook in the saved settings.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the test.</returns>
    [HttpPost("TestDiscord")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestDiscord(CancellationToken cancellationToken)
    {
        var url = Plugin.Instance?.Configuration.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new { Ok = false, Message = "Enter a Discord webhook URL first." };
        }

        try
        {
            await _discord.SendAsync(url, [DiscordMessageBuilder.BuildTest()], cancellationToken).ConfigureAwait(false);
            return new { Ok = true, Message = "Sent. Check your Discord channel." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { Ok = false, ex.Message };
        }
    }

    /// <summary>Stops reporting a series.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>No content.</returns>
    [HttpPost("Ignore/{seriesId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Ignore([FromRoute] Guid seriesId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NoContent();
        }

        var id = seriesId.ToString("N");
        if (!plugin.Configuration.IgnoredSeriesIds.Any(x => Guid.TryParse(x, out var g) && g == seriesId))
        {
            plugin.Configuration.IgnoredSeriesIds = [.. plugin.Configuration.IgnoredSeriesIds, id];
            plugin.SaveConfiguration();
        }

        _store.RemoveSeries(seriesId);
        return NoContent();
    }

    /// <summary>Reports a series again from the next check on.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>No content.</returns>
    [HttpPost("Unignore/{seriesId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Unignore([FromRoute] Guid seriesId)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.Configuration.IgnoredSeriesIds = plugin.Configuration.IgnoredSeriesIds
                .Where(x => !(Guid.TryParse(x, out var g) && g == seriesId))
                .ToArray();
            plugin.SaveConfiguration();
        }

        return NoContent();
    }

    /// <summary>Stops reporting a film collection.</summary>
    /// <param name="collectionId">TMDb collection id.</param>
    /// <param name="name">Name to show in the ignored list.</param>
    /// <returns>No content.</returns>
    [HttpPost("IgnoreCollection/{collectionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult IgnoreCollection([FromRoute] string collectionId, [FromQuery] string? name)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null && !plugin.Configuration.IgnoredCollections.Any(c => c.Id == collectionId))
        {
            plugin.Configuration.IgnoredCollections = [.. plugin.Configuration.IgnoredCollections, new IgnoredItem { Id = collectionId, Name = name ?? collectionId }];
            plugin.SaveConfiguration();
        }

        _store.RemoveFilms(collectionId, null);
        return NoContent();
    }

    /// <summary>Stops reporting one film.</summary>
    /// <param name="collectionId">TMDb collection id the film is listed under.</param>
    /// <param name="filmId">TMDb movie id.</param>
    /// <param name="name">Name to show in the ignored list.</param>
    /// <returns>No content.</returns>
    [HttpPost("IgnoreFilm/{collectionId}/{filmId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult IgnoreFilm([FromRoute] string collectionId, [FromRoute] int filmId, [FromQuery] string? name)
    {
        var plugin = Plugin.Instance;
        var id = filmId.ToString(CultureInfo.InvariantCulture);
        if (plugin is not null && !plugin.Configuration.IgnoredFilms.Any(f => f.Id == id))
        {
            plugin.Configuration.IgnoredFilms = [.. plugin.Configuration.IgnoredFilms, new IgnoredItem { Id = id, Name = name ?? id }];
            plugin.SaveConfiguration();
        }

        _store.RemoveFilms(collectionId, filmId);
        return NoContent();
    }

    /// <summary>Reports a film collection or film again from the next check on.</summary>
    /// <param name="kind">"Collection" or "Film".</param>
    /// <param name="id">TMDb id.</param>
    /// <returns>No content.</returns>
    [HttpPost("UnignoreTmdb/{kind}/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult UnignoreTmdb([FromRoute] string kind, [FromRoute] string id)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            if (kind == "Collection")
            {
                plugin.Configuration.IgnoredCollections = plugin.Configuration.IgnoredCollections.Where(c => c.Id != id).ToArray();
            }
            else
            {
                plugin.Configuration.IgnoredFilms = plugin.Configuration.IgnoredFilms.Where(f => f.Id != id).ToArray();
            }

            plugin.SaveConfiguration();
        }

        return NoContent();
    }

    /// <summary>Forgets the report, so the next check starts fresh (and sends a summary instead of "new" items).</summary>
    /// <returns>No content.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Reset()
    {
        _store.Clear();
        _guides.ClearCache();
        _activity.Info("Report cleared; the next check starts fresh");
        return NoContent();
    }
}
