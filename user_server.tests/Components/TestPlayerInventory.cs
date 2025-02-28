using network.common.data.models;
using user_server.players;

namespace user_server.tests.components;

public class TestPlayerInventory(GameUser user, PlayerInfo playerInfo, PlayerQuest playerQuest)
    : PlayerInventory(user, playerInfo, playerQuest)
{
    public bool SendUpdateItemsCalled { get; set; } = false;
    public List<ItemInfo> LastUpdatedItems { get; private set; } = new();

    public override void SendUpdateItems(List<ItemInfo> updateItems)
    {
        SendUpdateItemsCalled = true;
        LastUpdatedItems = updateItems;
        // 실제 업데이트는 수행하지 않음
    }
}