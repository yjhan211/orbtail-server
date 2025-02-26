using network.helpers;
using network.managers;

namespace game_server.services;

public class PacketQueueService(
    CacheHelper cacheHelper,
    Func<byte[], Task> messageHandler,
    LogManager logManager)
    : IAsyncDisposable
{
    private Task? _processingTask;
    private CancellationToken _cancellationToken;

    public void StartAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _processingTask = ProcessMessages();
    }

    private async Task ProcessMessages()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                var message = await cacheHelper.DequeueAsync("game_server_queue");
                if (message != null)
                {
                    await messageHandler(message);
                }
                else
                {
                    await Task.Delay(10, _cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logManager.WriteErrorLog(ex);
                await Task.Delay(1000, _cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_processingTask != null)
        {
            await _processingTask;
        }
    }
}