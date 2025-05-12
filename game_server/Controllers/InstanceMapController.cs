using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.config;
using network.helpers;
using network.interfaces;
using network.packets;

namespace game_server.controllers;

public class InstanceMapController : BaseMapController
{
   private readonly Dictionary<Protocol, Func<long, byte[], Task>> _protocolHandlers;
   private readonly ConcurrentDictionary<string, HashSet<string>> _objectInstanceDict = new();

   private string EnterInstanceSubject => 
       SubjectHelper.GetEnterInstanceSubject(ServerConfig.ServerId);

   public InstanceMapController(
       ILogger logger,
       INatsClient natsClient,
       CancellationTokenSource cts,
       ICacheHelper cacheHelper,
       ServerConfig serverConfig) 
       : base(logger, natsClient, cts, cacheHelper, serverConfig)
   {
       _protocolHandlers = new Dictionary<Protocol, Func<long, byte[], Task>>
       {
           { Protocol.U_TO_G_LOGOUT, HandleLogout }
       };
   }

   public void Initialize()
   {
       SubscribeWithHandler(EnterInstanceSubject, EnterInstance);
   }

   private void SubscribeToInstanceEvents(MapId mapId, long mapSubId)
   {
       var subjects = new Dictionary<string, Func<byte[], Task>>
       {
           { SubjectHelper.GetUpdateInfoSubject(mapId, mapSubId, ServerConfig.ServerId), HandleUpdateInfo },
           { SubjectHelper.GetSocialActionSubject(mapId, mapSubId, ServerConfig.ServerId), HandleSocialAction },
           { SubjectHelper.GetSpawnManageSubject(mapId, mapSubId, ServerConfig.ServerId), SpawnManageObject },
           { SubjectHelper.GetUpdateManageSubject(mapId, mapSubId, ServerConfig.ServerId), MoveManageObjectAsync },
           { SubjectHelper.GetLeaveManageSubject(mapId, mapSubId, ServerConfig.ServerId), LeaveManageObjectAsync },
           { SubjectHelper.GetDestroyObjectSubject(mapId, mapSubId, ServerConfig.ServerId), DestroyManageObjectAsync }
       };

       foreach (var (subject, handler) in subjects)
       {
           SubscribeWithHandler(subject, handler);
       }
   }

   private async Task EnterInstance(byte[] message)
   {
       var (objectKey, mapId, mapSubId, isLogin) = MessagePackSerializer.Deserialize<(string, MapId, long, bool)>(message);
       var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);

       await MapLock.WaitAsync();
       try
       {
           var isInit = _objectInstanceDict.TryAdd(instanceKey, []);
           _objectInstanceDict[instanceKey].Add(objectKey);
           
           if (isInit)
           {
               SubscribeToInstanceEvents(mapId, mapSubId);
               switch (mapId)
               {
                   case MapId.Camp:
                       break;
                   
                   default:
                       await InitializeExploreTargets(mapId, mapSubId);
                       break;
               }
           }
       }
       catch (Exception ex)
       {
           Logger.LogError(ex, "Error while entering instance");
       }
       finally
       {
           MapLock.Release();
       }

       if (!isLogin)
       {
           using var packet = PacketMaker.G_TO_U_ENTER_INSTANCE_SUCCESS(mapId, mapSubId);
           NatsClient.Publish(objectKey, packet.ToBytes());
       }
   }

   private async Task InitializeExploreTargets(MapId mapId, long mapSubId)
   {
       var exploreTargetList = GameExploreTargetData.GetListByMap(mapId);
       foreach (var exploreTarget in exploreTargetList)
       {
           var exploreTargetUid = await CacheHelper.StringIncrementAsync("temp_explore_target_uid");
           var objectInfo = new GameObjectInfo
           {
               ObjectType = ObjectType.EXPLORETARGET,
               ObjectId = exploreTargetUid,
               CurrentCell = exploreTarget.Position,
               TargetCell = exploreTarget.Position,
               MapId = mapId,
               MapSubId = mapSubId,
           };
                   
           var exploreTargetInfo = new ExploreTargetInfo(exploreTargetUid, exploreTarget.Id, objectInfo);
           var partKey = MapHelper.CreatePartKey(mapId, mapSubId);
           _objectInstanceDict.AddOrUpdate(partKey, [objectInfo.GetGameObjectKey()],
               (_, set) =>
               {
                   set.Add(objectInfo.GetGameObjectKey());
                   return set;
               });
                   
           await exploreTargetInfo.Save(CacheHelper);
           using var packet = PacketMaker.G_TO_U_UPDATE_OBJECT(objectInfo);
           BroadcastPacket(partKey, packet);
       }
   }

   private async Task HandleLogout(long playerId, byte[] body)
   {
       var msg = MessagePackSerializer.Deserialize<U_TO_G_LOGOUT>(body);
       
       await using var playerLock = await PlayerInfo.Lock(CacheHelper.GetRedLockFactory(), playerId);
       var playerInfo = await PlayerInfo.Load(CacheHelper, msg.PlayerId);
       if (playerInfo == null)
       {
           return;
       }

       // TODO: Implement logout logic
   }

   private async Task MoveManageObjectAsync(byte[] message)
   {
       var (_, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
       var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);
       var currentInstanceKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
       
       await UpdateObjectPositionAsync(currentInstanceKey, objectKey);

       using var packet = PacketMaker.G_TO_U_UPDATE_OBJECT(objectInfo);
       BroadcastPacket(currentInstanceKey, packet);
   }

   private async Task UpdateObjectPositionAsync(string currentInstanceKey, string objectKey)
   {
       await MapLock.WaitAsync();
       try
       {
           _objectInstanceDict.AddOrUpdate(
               currentInstanceKey,
               [objectKey],
               (_, set) =>
               {
                   set.Add(objectKey);
                   return set;
               }
           );
       }
       finally
       {
           MapLock.Release();
       }
   }

   private async Task LeaveManageObjectAsync(byte[] message)
   {
       var (instanceKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
       await MapLock.WaitAsync();
       try
       {
           if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet)) 
               instanceSet.Remove(objectKey);
       }
       finally
       {
           MapLock.Release();
       }
   }

   private Task SpawnManageObject(byte[] message)
   {
       var (objectKey, instanceKeyList, cellsToRemove) =
           MessagePackSerializer.Deserialize<(string, List<string>, List<Cell>)>(message);

       var spawnList = new List<string>();
       foreach (var instancePartKey in instanceKeyList)
       {
           if (_objectInstanceDict.TryGetValue(instancePartKey, out var objectKeys))
           {
               spawnList.AddRange(objectKeys);
           }
       }

       if (spawnList.Count <= 0)
       {
           return Task.CompletedTask;
       }
       
       using var packet = PacketMaker.G_TO_U_SPAWN(spawnList, []);
       NatsClient.Publish(objectKey, packet.ToBytes());

       return Task.CompletedTask;
   }

   private async Task DestroyManageObjectAsync(byte[] message)
   {
       var (instanceKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);

       await MapLock.WaitAsync();
       try
       {
           if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
           {
               instanceSet.Remove(objectKey);
           }
       }
       finally
       {
           MapLock.Release();
       }

       using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
       BroadcastPacket(instanceKey, packet);
   }

   protected override void BroadcastPacket(string instanceKey, IPacket packet)
   {
       if (!_objectInstanceDict.TryGetValue(instanceKey, out var channels))
       {
           return;
       }

       var channelsCopy = channels.ToList();
       foreach (var channel in channelsCopy)
       {
           NatsClient.Publish(channel, packet.ToBytes());
       }
   }

   public override async Task ShutdownAsync()
   {
       await MapLock.WaitAsync();
       MapLock.Release();
   }
}
