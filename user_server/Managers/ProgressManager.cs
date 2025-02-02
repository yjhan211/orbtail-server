using System.Collections.Concurrent;
using network.common.data.models;
using network.managers;

namespace user_server.managers;

public interface IProgressTrackable
{
    DateTime GetEndTime();
}

public class ProgressItem(IProgressTrackable trackable, Func<IProgressTrackable, Task> onComplete)
{
    public IProgressTrackable Trackable { get; } = trackable;
    public Func<IProgressTrackable, Task> OnComplete { get; } = onComplete;
}

public class ExploreProgressInfo(ExploreTargetInfo exploreTargetInfo) : IProgressTrackable
{
    public ExploreTargetInfo ExploreTargetInfo { get; } = exploreTargetInfo;

    public DateTime GetEndTime()
    {
        return ExploreTargetInfo.EndTimestamp;
    }
}

public class CraftProgressInfo(int craftId, DateTime endTimestamp) : IProgressTrackable
{
    public readonly int CraftId = craftId;
    public DateTime GetEndTime()
    {
        return endTimestamp;
    }
}

public sealed class ProgressManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, ProgressItem> _activeProgressItems = new();
    private readonly Timer _cleanupTimer;
    private readonly LogManager? _logManager;
    private bool _disposed;

    public ProgressManager(LogManager? logManager)
    {
        _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _logManager = logManager;
    }

    public void Dispose()
    {
        Dispose(true);
    }

    public void AddProgressItem(IProgressTrackable item, Func<IProgressTrackable, Task> onComplete)
    {
        var id = Guid.NewGuid();
        _activeProgressItems[id] = new ProgressItem(item, onComplete);
    }

    private async void CleanupExpiredItems(object? _)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredItems = _activeProgressItems.Where(kvp => kvp.Value.Trackable.GetEndTime() <= now).ToList();

            foreach (var kvp in expiredItems)
                if (_activeProgressItems.TryRemove(kvp.Key, out var item))
                    try
                    {
                        await item.OnComplete(item.Trackable);
                    }
                    catch (Exception ex)
                    {
                        _logManager.WriteErrorLog(ex);
                    }
        }
        catch (Exception ex)
        {
            _logManager.WriteErrorLog(ex);
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing) _cleanupTimer.Dispose();

        _disposed = true;
    }
}