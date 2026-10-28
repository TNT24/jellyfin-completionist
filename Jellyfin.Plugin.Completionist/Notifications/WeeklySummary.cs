using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Completionist.Configuration;
using Jellyfin.Plugin.Completionist.Scanning;

namespace Jellyfin.Plugin.Completionist.Notifications;

/// <summary>
/// Sends the weekly summary to Discord, from the latest saved report.
/// </summary>
public sealed class WeeklySummary
{
    private readonly ReportStore _store;
    private readonly DiscordNotifier _discord;
    private readonly ActivityLog _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WeeklySummary"/> class.
    /// </summary>
    /// <param name="store">Report store.</param>
    /// <param name="discord">Discord notifier.</param>
    /// <param name="activity">Activity log.</param>
    public WeeklySummary(ReportStore store, DiscordNotifier discord, ActivityLog activity)
    {
        _store = store;
        _discord = discord;
        _activity = activity;
    }

    /// <summary>Sends the summary.</summary>
    /// <param name="force">Send even when the weekly summary is switched off (the settings page's button).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Why nothing was sent, or null when it was sent. Throws if Discord refuses it.</returns>
    public async Task<string?> SendAsync(bool force, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!force && !config.WeeklySummary)
        {
            return "The weekly summary is switched off.";
        }

        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
        {
            return "Enter a Discord webhook URL first.";
        }

        if (_store.Get() is not { } report)
        {
            return "There's nothing to summarise until the first check has run.";
        }

        await _discord.SendAsync(config.DiscordWebhookUrl, [DiscordMessageBuilder.BuildWeeklySummary(report, DateTime.UtcNow, config.PublicServerUrl)], cancellationToken).ConfigureAwait(false);
        _activity.Info("Sent the weekly summary to Discord");
        return null;
    }
}
