using game_server.players.bots;
using game_server.field;
using game_server.orbs;
using game_server.matches;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerEliminationCauseTests
{
    [Fact]
    public void PlayerEliminatedMessage_RoundTripsAttackerPlayerId()
    {
        var source = new G_TO_C_PLAYER_ELIMINATED
        {
            PlayerId = 20,
            AttackerPlayerId = 10,
            Reason = EliminationReason.HEALTH_ZERO
        };

        byte[] bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<G_TO_C_PLAYER_ELIMINATED>(bytes);

        Assert.Equal(20, result.PlayerId);
        Assert.Equal(10, result.AttackerPlayerId);
        Assert.Equal(EliminationReason.HEALTH_ZERO, result.Reason);
    }

    [Fact]
    public void GameResultPlayerInfo_RoundTripsRankTierAndEnvironmentalCause()
    {
        var source = new GameResultPlayerInfo
        {
            PlayerId = 20,
            Rank = 4,
            FinalOrbTier = 3,
            EliminationReason = EliminationReason.PRESSURE_FIELD
        };

        byte[] bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<GameResultPlayerInfo>(bytes);

        Assert.Equal(4, result.Rank);
        Assert.Equal(3, result.FinalOrbTier);
        Assert.Equal(EliminationReason.PRESSURE_FIELD, result.EliminationReason);
    }
    [Fact]
    public void BotDamage_RemembersFirstAttackerThatReachesEliminationThreshold()
    {
        var manager = new BotPlayerManager(1, NullLogger.Instance, new DoorState(), new SunOrbAttackState(1), TestGameEventLogs.Create());
        var bot = new BotPlayerState
        {
            PlayerId = -1,
            Player = { Health = 10 }
        };

        manager.ApplyProximityAutoCombatDamage(bot, 9, attackerPlayerId: 101);
        Assert.Equal(0, bot.LastProximityAttackerPlayerId);

        manager.ApplyProximityAutoCombatDamage(bot, 1, attackerPlayerId: 102);
        Assert.Equal(102, bot.LastProximityAttackerPlayerId);

        manager.ApplyProximityAutoCombatDamage(bot, 10, attackerPlayerId: 103);
        Assert.Equal(102, bot.LastProximityAttackerPlayerId);
    }

}
