using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.helpers;

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
            Reason = EliminationReason.MENTAL_ZERO
        };

        byte[] bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<G_TO_C_PLAYER_ELIMINATED>(bytes);

        Assert.Equal(20, result.PlayerId);
        Assert.Equal(10, result.AttackerPlayerId);
        Assert.Equal(EliminationReason.MENTAL_ZERO, result.Reason);
    }

    [Fact]
    public void GameResultPlayerInfo_RoundTripsRankTierAndEnvironmentalCause()
    {
        var source = new GameResultPlayerInfo
        {
            PlayerId = 20,
            Rank = 4,
            FinalOrbTier = 3,
            IsAreaClosureElimination = false,
            IsOvertimeElimination = true
        };

        byte[] bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<GameResultPlayerInfo>(bytes);

        Assert.Equal(4, result.Rank);
        Assert.Equal(3, result.FinalOrbTier);
        Assert.False(result.IsAreaClosureElimination);
        Assert.True(result.IsOvertimeElimination);
    }
    [Fact]
    public void BotDamage_RemembersFirstAttackerThatReachesEliminationThreshold()
    {
        var manager = new BotPlayerManager(NullLogger.Instance);
        var bot = new BotPlayerState
        {
            PlayerId = -1,
            Corruption = Config.SURVIVOR_MAX_CORRUPTION - 10
        };

        manager.ApplyProximityAutoCombatDamage(bot, 9, attackerPlayerId: 101);
        Assert.Equal(0, bot.LastProximityAttackerPlayerId);

        manager.ApplyProximityAutoCombatDamage(bot, 1, attackerPlayerId: 102);
        Assert.Equal(102, bot.LastProximityAttackerPlayerId);

        manager.ApplyProximityAutoCombatDamage(bot, 10, attackerPlayerId: 103);
        Assert.Equal(102, bot.LastProximityAttackerPlayerId);
    }

    [Fact]
    public void GameResultPacketChunker_SplitsRosterWithinPacketBudget()
    {
        var players = Enumerable.Range(1, 8)
            .Select(playerId => new GameResultPlayerInfo
            {
                PlayerId = playerId,
                Name = $"Player{playerId:0000}_{new string('x', 300)}",
                WearItemIdList = new List<int> { 1001, 1002, 1003, 1004 },
                KillCount = playerId,
                TotalDamageDealt = playerId * 100,
                TotalRecovery = playerId * 10,
                SurvivalTimeSeconds = playerId * 60
            })
            .ToList();

        var eliminationChunks = GameResultPacketChunker.CreateEliminationChunks(
            playerId: 1,
            attackerPlayerId: 2,
            EliminationReason.MENTAL_ZERO,
            players);
        var gameResultChunks = GameResultPacketChunker.CreateGameResultChunks(
            winnerId: 1,
            isTimeout: false,
            players);

        Assert.True(eliminationChunks.Count > 1);
        Assert.True(gameResultChunks.Count > 1);
        Assert.All(eliminationChunks, chunk =>
            Assert.True(
                MessagePackSerializer.Serialize(chunk).Length <= Config.BUFFER_SIZE - Config.HEADER_SIZE));
        Assert.All(gameResultChunks, chunk =>
            Assert.True(
                MessagePackSerializer.Serialize(chunk).Length <= Config.BUFFER_SIZE - Config.HEADER_SIZE));
        Assert.Equal(players.Select(player => player.PlayerId),
            eliminationChunks.SelectMany(chunk => chunk.ResultPlayers).Select(player => player.PlayerId));
        Assert.Equal(players.Select(player => player.PlayerId),
            gameResultChunks.SelectMany(chunk => chunk.Players).Select(player => player.PlayerId));
        Assert.All(eliminationChunks.Take(eliminationChunks.Count - 1), chunk => Assert.False(chunk.IsResultEnd));
        Assert.True(eliminationChunks[^1].IsResultEnd);
        Assert.All(gameResultChunks.Take(gameResultChunks.Count - 1), chunk => Assert.False(chunk.IsResultEnd));
        Assert.True(gameResultChunks[^1].IsResultEnd);
    }
}
