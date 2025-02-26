using MessagePack;
using network.common;
using network.common.data.models;
using network.config;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.managers;
using network.packets;
using StackExchange.Redis;

namespace game_server.controllers;

public abstract class BaseMapController(
    LogManager logManager,
    NatsClient natsClient,
    CancellationTokenSource cts,
    CacheHelper cacheHelper,
    ServerConfig serverConfig)
{
   protected readonly LogManager LogManager = logManager;
   protected readonly NatsClient NatsClient = natsClient;
   protected readonly CancellationTokenSource Cts = cts;
   protected readonly CacheHelper CacheHelper = cacheHelper;
   protected readonly ServerConfig ServerConfig = serverConfig;
   protected readonly SemaphoreSlim MapLock = new(1, 1);

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
                   LogManager.WriteErrorLog(ex);
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
           case ObjectType.CAMP:
           {
               var campInfo = MessagePackSerializer.Deserialize<CampInfo>(serializedInfo);
               using var packet = PacketMaker.G_TO_U_CAMP_INFO(campInfo);
               BroadcastPacket(key, packet);
               break;
           }
       }
       return Task.CompletedTask;
   }

   protected Task HandleSocialAction(byte[] message)
   {
       var (partKey, (playerId, socialActionType)) = 
           MessagePackSerializer.Deserialize<(string, (long, SocialActionType))>(message);
       using var packet = PacketMaker.G_TO_U_SOCIAL_ACTION(playerId, socialActionType);
       BroadcastPacket(partKey, packet);
       return Task.CompletedTask;
   }

   protected abstract void BroadcastPacket(string key, IPacket packet);

   public abstract Task ShutdownAsync();
}
