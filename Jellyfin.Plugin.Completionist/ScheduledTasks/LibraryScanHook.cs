using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Completionist.Scanning;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Completionist.ScheduledTasks;

/// <summary>
/// Runs a check after every library scan, so episodes you just added drop off the list straight away.
/// Episode lists are cached, so this rarely calls TVmaze or TMDb.
/// </summary>
public class LibraryScanHook : ILibraryPostScanTask
{
    private readonly CompletionistScanner _scanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanHook"/> class.
    /// </summary>
    /// <param name="scanner">The scanner.</param>
    public LibraryScanHook(CompletionistScanner scanner)
    {
        _scanner = scanner;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.CheckOnLibraryScan != true)
        {
            return Task.CompletedTask;
        }

        // Run in the background so the first check over a big library doesn't hold up the scan.
        _scanner.RunInBackground();
        return Task.CompletedTask;
    }
}
