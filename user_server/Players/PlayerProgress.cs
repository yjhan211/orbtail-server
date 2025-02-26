using System.Collections.Concurrent;
using network.interfaces;
using network.managers;

namespace user_server.players;

public sealed class PlayerProgress : IDisposable
{
    private readonly LogManager _logManager;
    private readonly ConcurrentDictionary<Guid, ProgressItem> _activeProgressItems = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public PlayerProgress(GameUser user)
    {
        _logManager = user.LogManager;
        _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
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
            var expiredItems = _activeProgressItems
                .Where(kvp => kvp.Value.Trackable.GetEndTime() <= now)
                .ToList();
        
            foreach (var kvp in expiredItems)
            {
                if (_activeProgressItems.TryRemove(kvp.Key, out var item))
                {
                    await ProcessExpiredItem(item);
                }
            }
        }
        catch (Exception ex)
        {
            _logManager.WriteErrorLog(ex);
        }
    }

    private async Task ProcessExpiredItem(ProgressItem item)
    {
        try
        {
            await item.OnComplete(item.Trackable);
        }
        catch (Exception ex)
        {
            _logManager.WriteErrorLog(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
            
        _cleanupTimer.Dispose();
        _activeProgressItems.Clear();
        _disposed = true;
    }
}