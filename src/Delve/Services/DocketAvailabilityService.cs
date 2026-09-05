using System.Diagnostics;

namespace Delve.Services;

public enum DocketAvailability
{
    /// docket.exe isn't running, or the index has no entries yet - hotkey stays inactive.
    Unavailable,

    /// docket.exe is running and its index has at least one entry.
    Available,
}

public sealed class DocketAvailabilityChangedEventArgs : EventArgs
{
    public DocketAvailabilityChangedEventArgs(DocketAvailability availability) => Availability = availability;
    public DocketAvailability Availability { get; }
}

/// Polls for whether Docket's search index is usable right now. Docket's "Search Everywhere"
/// index (SearchIndexService) isn't a separate Windows Service - it's a static class that runs
/// inside the docket.exe process and keeps the SQLite file fresh via a FileSystemWatcher only
/// while that process is alive. So "the search index service is running" is treated here as:
/// the docket.exe process exists AND the index file has at least one row.
///
/// Polling (not a FileSystemWatcher on the db, not a process-exit event) is deliberate and
/// simple: this only needs to notice a state change within a few seconds, and Docket
/// starting/stopping is a rare, human-paced event - not worth the added complexity of a
/// WMI process-watcher or a file-system watcher on a file this class only ever reads.
public sealed class DocketAvailabilityService : IDisposable
{
    private const string DocketProcessName = "docket";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly DocketIndexReader _reader;
    private readonly Timer _timer;
    private DocketAvailability _current = DocketAvailability.Unavailable;

    public event EventHandler<DocketAvailabilityChangedEventArgs>? AvailabilityChanged;

    public DocketAvailabilityService(DocketIndexReader reader)
    {
        _reader = reader;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public DocketAvailability Current => _current;

    public void Start()
    {
        Poll();
        _timer.Change(PollInterval, PollInterval);
    }

    private void Poll()
    {
        var next = IsDocketRunning() && _reader.TryGetEntryCount() > 0
            ? DocketAvailability.Available
            : DocketAvailability.Unavailable;

        if (next != _current)
        {
            _current = next;
            AvailabilityChanged?.Invoke(this, new DocketAvailabilityChangedEventArgs(next));
        }
    }

    private static bool IsDocketRunning()
    {
        var processes = Process.GetProcessesByName(DocketProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
