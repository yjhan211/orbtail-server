using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.interfaces;
using network.packets;

namespace game_server.controllers;

public abstract class BaseMapController(
    ILogger logger,
    INatsClient natsClient,
    ICacheHelper cacheHelper,
    IServerConfig serverConfig)
{
    protected readonly ILogger Logger = logger;
    protected readonly INatsClient NatsClient = natsClient;
    protected readonly ICacheHelper CacheHelper = cacheHelper;
    protected readonly SemaphoreSlim MapLock = new(1, 1);
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
