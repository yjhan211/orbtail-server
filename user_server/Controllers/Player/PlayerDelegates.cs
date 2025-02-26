using network.common;

namespace user_server.controllers.player;

using network.common.data.models;
using network.packets;

public delegate void SendPacketDelegate(Packet packet);
public delegate void BroadcastDelegate<in T>(T info) where T : IMessagePackObject?;
public delegate void BroadcastDestroyDelegate(GameObjectInfo info);
public delegate void BroadcastSocialActionDelegate(PlayerInfo info, SocialActionType actionType);