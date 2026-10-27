using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Completionist.Scanning;

/// <summary>Severity of an activity log entry.</summary>
public enum ActivityLevel
{
    /// <summary>Normal progress.</summary>
    Info,

    /// <summary>Something the user may want to act on.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error
}

/// <summary>One line of the activity log.</summary>
/// <param name="Time">When it happened (UTC).</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">What happened.</param>
public sealed record ActivityEntry(DateTime Time, ActivityLevel Level, string Message);

/// <summary>
/// The plugin's own log, shown on its settings page. Every entry also goes to Jellyfin's server log.
/// Kept in memory, so it starts empty after a server restart.
/// </summary>
public sealed class ActivityLog
{
    private const int Capacity = 500;

    private readonly object _lock = new();
    private readonly Queue<ActivityEntry> _entries = new();
    private readonly ILogger<ActivityLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityLog"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ActivityLog(ILogger<ActivityLog> logger)
    {
        _logger = logger;
    }

    /// <summary>Records normal progress.</summary>
    /// <param name="message">What happened.</param>
    public void Info(string message)
    {
        _logger.LogInformation("Completionist: {Message}", message);
        Add(ActivityLevel.Info, message);
    }

    /// <summary>Records something the user may want to act on.</summary>
    /// <param name="message">What happened.</param>
    public void Warning(string message)
    {
        _logger.LogWarning("Completionist: {Message}", message);
        Add(ActivityLevel.Warning, message);
    }

    /// <summary>Records a failure. The exception's details go to the server log only.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="exception">The cause, if any.</param>
    public void Error(string message, Exception? exception = null)
    {
        _logger.LogError(exception, "Completionist: {Message}", message);
        Add(ActivityLevel.Error, exception is null ? message : message + ": " + exception.Message);
    }

    /// <summary>Returns the entries, newest first.</summary>
    /// <returns>The entries.</returns>
    public List<ActivityEntry> Recent()
    {
        lock (_lock)
        {
            return _entries.Reverse().ToList();
        }
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void Add(ActivityLevel level, string message)
    {
        lock (_lock)
        {
            _entries.Enqueue(new ActivityEntry(DateTime.UtcNow, level, message));
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }
}
