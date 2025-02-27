using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.interfaces;

namespace user_server.players;

public sealed class PlayerProgress : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, ProgressItem> _activeProgressItems = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public PlayerProgress(GameUser user)
    {
        _logger = user.Logger;
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
            _logger.LogError(ex, "Error cleaning up expired items");
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
            _logger.LogError(ex, "Error processing expired item");
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