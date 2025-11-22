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
    CancellationTokenSource cts,
    ICacheHelper cacheHelper,
    IServerConfig serverConfig)
{
    protected readonly ILogger Logger = logger;
    protected readonly INatsClient NatsClient = natsClient;
    protected readonly CancellationTokenSource Cts = cts;
    protected readonly ICacheHelper CacheHelper = cacheHelper;
    protected readonly SemaphoreSlim MapLock = new(1, 1);
    public readonly IServerConfig ServerConfig = serverConfig;

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

    protected Task HandleUpdateInfo(byte[] message)
    {
        var (key, type, serializedInfo) = MessagePackSerializer.Deserialize<(string, ObjectType, byte[])>(message);
        switch (type)
        {
            case ObjectType.PLAYER:
                {
                    var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serializedInfo);
                    using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
                    BroadcastPacket(key, packet);
                    break;
                }
            case ObjectType.EXPLORETARGET:
                {
                    var exploreTargetInfo = MessagePackSerializer.Deserialize<ExploreTargetInfo>(serializedInfo);
                    using var packet = PacketMaker.G_TO_U_EXPLORE_TARGET_INFO(exploreTargetInfo);
                    BroadcastPacket(key, packet);
                    break;
                }
        }
        return Task.CompletedTask;
    }

    protected Task HandleSocialAction(byte[] message)
    {
        var (partKey, type, serializedInfo) = MessagePackSerializer.Deserialize<(string, ObjectType, byte[])>(message);
        var (playerId, socialActionType) = MessagePackSerializer.Deserialize<(long, SocialActionType)>(serializedInfo);
        using var packet = PacketMaker.G_TO_U_SOCIAL_ACTION(playerId, socialActionType);
        BroadcastPacket(partKey, packet);

        return Task.CompletedTask;
    }

    protected Task HandleTakeDamage(byte[] message)
    {
        var (partKey, type, serializedInfo) = MessagePackSerializer.Deserialize<(string, ObjectType, byte[])>(message);
        var (playerId, (damageType, damageAmount)) = MessagePackSerializer.Deserialize<(long, (DamageType, int))>(serializedInfo);
        using var packet = PacketMaker.G_TO_U_TAKE_DAMAGE(playerId, damageType, damageAmount);
        BroadcastPacket(partKey, packet);

        return Task.CompletedTask;
    }

    protected abstract void BroadcastPacket(string key, IPacket packet);

    public abstract Task ShutdownAsync();
}
