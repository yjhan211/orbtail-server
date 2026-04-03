using Microsoft.Extensions.Logging;
using network.interfaces;

namespace game_server.controllers;

public abstract class BaseMapManager(
    ILogger logger,
    INatsClient natsClient,
    ICacheHelper cacheHelper,
    IServerConfig serverConfig)
{
    protected readonly ICacheHelper CacheHelper = cacheHelper;
    protected readonly ILogger Logger = logger;
    protected readonly SemaphoreSlim MapLock = new(1, 1);
    protected readonly INatsClient NatsClient = natsClient;
    protected readonly IServerConfig ServerConfig = serverConfig;

    protected void SubscribeWithHandler(string subject, Func<byte[], Task> handler)
    {
        NatsClient.Subscribe(subject, (_, msg) =>
        {
            Task.Run(async () =>
            {
                try
                {
                    await handler(msg);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Unhandled exception in handler");
                }
            });
        });
    }

    protected abstract void BroadcastPacket(string key, IPacket packet);
}
