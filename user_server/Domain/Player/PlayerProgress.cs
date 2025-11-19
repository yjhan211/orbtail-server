using user_server.infrastructure.network;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.interfaces;
using user_server.domain.valueobjects;

namespace user_server.domain.player;

public class PlayerProgress : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, ProgressItem> _activeProgressItems = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public PlayerProgress(GameSession user)
    {
        _logger = user.Logger;
        _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public virtual void AddProgressItem(IProgressTrackable item, Func<IProgressTrackable, Task> onComplete)
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
    
    private int CancelExploreProgress()
    {
        var canceledCount = 0;
        var itemsToCancel = new List<KeyValuePair<Guid, ProgressItem>>();

        try
        {
            // 해당 플레이어의 탐험 진행 항목들을 찾습니다
            foreach (var kvp in _activeProgressItems)
            {
                itemsToCancel.Add(kvp);
            }

            // 찾은 항목들을 제거하고 정리합니다
            foreach (var kvp in itemsToCancel)
            {
                if (_activeProgressItems.TryRemove(kvp.Key, out var item))
                {
                    canceledCount++;
                }
            }
        }
        catch (Exception ex)
        {
            throw;
        }

        return canceledCount;
    }


    public void Dispose()
    {
        if (_disposed)
            return;

        var cancelNum = CancelExploreProgress();
        _logger.LogInformation("PlayerProgress: {CancelNum} items canceled", cancelNum);
        
        _cleanupTimer.Dispose();
        _activeProgressItems.Clear();
        _disposed = true;
    }
}