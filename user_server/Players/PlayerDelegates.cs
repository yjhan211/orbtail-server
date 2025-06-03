using network.common;
using network.interfaces;

namespace user_server.players;

using network.common.data.models;
using network.packets;

public delegate void SendPacketDelegate(Packet packet);
public delegate void BroadcastDelegate<in T>(T info) where T : IMessagePackObject?;
public delegate void BroadcastDestroyDelegate(GameObjectInfo info);
public delegate void BroadcastSocialActionDelegate(PlayerInfo info, SocialActionType actionType);
public delegate void BroadcastTakeDamageDelegate(PlayerInfo playerInfo, DamageType damageType, int damage);
public delegate Task IncreaseQuestCountDelegate(int questId, int count, List<QuestInfo> updateQuests);
public delegate Task StartQuestDelegate(int questId, List<QuestInfo> updateQuests);
public delegate void SendUpdateItemsDelegate(List<ItemInfo> updateItems);
public delegate void AddProgressItemDelegate(IProgressTrackable item, Func<IProgressTrackable, Task> onComplete);
