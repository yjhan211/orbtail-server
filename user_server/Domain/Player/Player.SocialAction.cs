using network.common;
using network.common.data;
using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Social Action 관련 기능
/// </summary>
public partial class Player
{
    public async Task PerformSocialAction(C_TO_U_SOCIAL_ACTION body)
    {
        switch (body.SocialActionType)
        {
            case SocialActionType.SITGROUND:
                PlayerInfo.State = PlayerInfo.State == PlayerState.IDLE
                    ? PlayerState.SITGROUND
                    : PlayerState.IDLE;
                await PlayerInfo.Save(_cacheHelper);
                _broadcastPlayerInfo(PlayerInfo);
                break;

            default:
                _broadcastSocialAction(PlayerInfo, body.SocialActionType);
                break;
        }
    }

    public async Task SetName(C_TO_U_SET_NAME body)
    {
        PlayerInfo.Name = body.Name;
        await PlayerInfo.Save(_cacheHelper);
        _broadcastPlayerInfo(PlayerInfo);
    }
}
