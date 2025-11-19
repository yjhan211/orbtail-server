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

    public async Task SubscribeEnvironment(G_TO_U_ENVIRONMENT body)
    {
        const int damage = 2000;
        if (PlayerInfo.PlayerId > PlayerConstants.DUMMY_PLAYER_ID_THRESHOLD)
        {
            return;
        }

        if (PlayerInfo.State == PlayerState.SLEEP)
        {
            return;
        }

        if (PlayerInfo.QuestDiary.QuestDict.TryGetValue(100000007, out var questInfo))
        {
            if (questInfo.State != QuestState.END)
            {
                return;
            }
        }

        if (PlayerInfo.WearItemIdList.Contains(107000001))
        {
            return;
        }

        PlayerInfo.Hp = Math.Max(0, PlayerInfo.Hp - damage);
        if (PlayerInfo.Hp <= 0)
        {
            PlayerInfo.State = PlayerState.SLEEP;
        }

        await PlayerInfo.Save(_cacheHelper);

        _broadcastPlayerInfo(PlayerInfo);
        _broadcastTakeDamage(PlayerInfo, DamageType.DARK, damage);
    }

    public async Task SetName(C_TO_U_SET_NAME body)
    {
        PlayerInfo.Name = body.Name;
        await PlayerInfo.Save(_cacheHelper);
        _broadcastPlayerInfo(PlayerInfo);
    }
}
