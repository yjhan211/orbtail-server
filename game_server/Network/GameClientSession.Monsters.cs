using game_server.services;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private void SendMonsterSnapshot()
    {
        if (CurrentMapSubId <= 0)
            return;

        SendMonsterSnapshot(_emotionAfterimageMonsterManager.GetSnapshot(CurrentMapSubId));
    }

    private void SendMonsterSnapshot(AreaType area)
    {
        if (CurrentMapSubId <= 0 || area == AreaType.None)
            return;

        SendMonsterSnapshot(_emotionAfterimageMonsterManager.GetSnapshot(CurrentMapSubId, area));
    }

    private void SendMonsterSnapshot(IEnumerable<MonsterRuntimeInfo> states)
    {
        foreach (var monsterChunk in MonsterSnapshotBatcher.CreateAreaChunks(states))
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = monsterChunk.Monsters
            }));
            Send(packet);
        }
    }
}
