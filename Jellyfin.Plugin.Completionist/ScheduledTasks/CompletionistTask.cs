using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Completionist.Scanning;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Completionist.ScheduledTasks;

/// <summary>
/// Scheduled task: checks every show for missing seasons and episodes, and every film collection for missing films.
/// Appears under Dashboard → Scheduled Tasks → Library.
/// </summary>
public class CompletionistTask : IScheduledTask
{
    private readonly CompletionistScanner _scanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompletionistTask"/> class.
    /// </summary>
    /// <param name="scanner">The scanner.</param>
    public CompletionistTask(CompletionistScanner scanner)
    {
        _scanner = scanner;
    }

    /// <inheritdoc />
    public string Name => "Find missing episodes and films";

    /// <inheritdoc />
    public string Description => "Compares your TV shows with TVmaze or TMDb, and your film collections with TMDb, and reports what you don't have.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CompletionistScan";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _scanner.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily at 5am, after most shows' new episodes have aired the evening before.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
        };
    }
}
