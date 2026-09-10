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
    public void BotDamageEliminatesImmediatelyAndKeepsLethalAttacker()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(1);
        var eliminations = TestGameSessionServices.CreateEliminationService(store, store.EventLogs,
            new game_server.matches.results.MatchSummaryFileStore(), NullLogger.Instance);
        var bot = new BotPlayerState { PlayerId = -1, Player = { Health = 10 } };
        using (match.Enter())
        {
            match.Bots.GetBots(1).Add(bot);
            match.RegisterParticipant(bot.Player);
            match.CombatDamage.ApplyProximityAutoCombatHit(eliminations, bot.Player, 101, AreaType.None, 123, 9);
            Assert.False(bot.Player.IsEliminated);
            match.CombatDamage.ApplyProximityAutoCombatHit(eliminations, bot.Player, 102, AreaType.None, 123, 1);
            Assert.True(bot.Player.IsEliminated);
            Assert.Equal(102, bot.Player.AttackerPlayerId);
            int rank = bot.Player.EliminationRank;
            match.CombatDamage.ApplyProximityAutoCombatHit(eliminations, bot.Player, 103, AreaType.None, 123, 10);
            Assert.Equal(102, bot.Player.AttackerPlayerId);
            Assert.Equal(rank, bot.Player.EliminationRank);
            Assert.False(bot.Player.CanSleep(DateTime.UtcNow));
        }
    }

}
