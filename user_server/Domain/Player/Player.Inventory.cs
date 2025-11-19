using network.common;
using network.common.data;
using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Inventory 관련 기능
/// </summary>
public partial class Player
{
    public async Task WearItem(C_TO_U_WEAR_ITEM body)
    {
        await _inventoryManager.RequestWearItem(body);
    }

    public async Task UseItem(C_TO_U_USE_ITEM body)
    {
        await _inventoryManager.RequestUseItem(body);
    }

    public void SendCurrentItems()
    {
        _inventoryManager.SendCurrentItems();
    }
}
