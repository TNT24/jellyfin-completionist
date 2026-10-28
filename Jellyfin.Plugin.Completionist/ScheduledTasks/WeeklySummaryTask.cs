using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Completionist.Notifications;
using Jellyfin.Plugin.Completionist.Scanning;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Completionist.ScheduledTasks;

/// <summary>
/// Scheduled task: posts the weekly summary to Discord, when it's switched on.
/// Appears under Dashboard → Scheduled Tasks → Library, where its day and time can be changed.
/// </summary>
public class WeeklySummaryTask : IScheduledTask
{
    private readonly WeeklySummary _summary;
    private readonly ActivityLog _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WeeklySummaryTask"/> class.
    /// </summary>
    /// <param name="summary">Weekly summary sender.</param>
    /// <param name="activity">Activity log.</param>
    public WeeklySummaryTask(WeeklySummary summary, ActivityLog activity)
    {
        _summary = summary;
        _activity = activity;
    }

    /// <inheritdoc />
    public string Name => "Send Completionist weekly summary";

    /// <inheritdoc />
    public string Description => "Posts a summary of the week to Discord: what went missing, what turned up and what's still missing. Does nothing unless the weekly summary is switched on in Completionist's settings.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CompletionistWeeklySummary";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var skipped = await _summary.SendAsync(false, cancellationToken).ConfigureAwait(false);
            if (skipped is not null && !skipped.Contains("switched off", StringComparison.Ordinal))
            {
                _activity.Info("Weekly summary not sent: " + skipped);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activity.Error("Couldn't post the weekly summary to Discord", ex);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Sunday morning, after that day's 5am check.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(9).Ticks
        };
    }
}
