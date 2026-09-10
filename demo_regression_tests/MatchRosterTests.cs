using game_server.matches;
using game_server.matches.results;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class MatchRosterTests
{
    [Fact]
    public void Elimination_OnlyChangesTheEliminatedPlayer_WhenLegacyChainEffectsAreDisabled()
    {
        const long matchingId = 194001;
        var manager = MatchTestServices.Runtime(matchingId, NullLogger.Instance);

        manager.RegisterParticipant(CreateLink(1, 2));
        manager.RegisterParticipant(CreateLink(2, 3));
        manager.RegisterParticipant(CreateLink(3, 1));

        bool eliminated = manager.TryEliminatePlayer(2, EliminationReason.HEALTH_ZERO);

        Assert.True(eliminated);
        Assert.Equal(PlayerMatchStatus.ELIMINATED, manager.BuildGameResult().Single(row => row.playerId == 2).finalStatus);
        Assert.Equal(PlayerMatchStatus.ACTIVE, manager.BuildGameResult().Single(row => row.playerId == 1).finalStatus);
        Assert.Equal(PlayerMatchStatus.ACTIVE, manager.BuildGameResult().Single(row => row.playerId == 3).finalStatus);
    }

    [Fact]
    public void Elimination_PreservesAttackerAndAreaInGameResult()
    {
        const long matchingId = 194002;
        var manager = MatchTestServices.Runtime(matchingId, NullLogger.Instance);

        manager.RegisterParticipant(CreateLink(1, 2));
        manager.RegisterParticipant(CreateLink(2, 1));

        manager.TryEliminatePlayer(2, EliminationReason.HEALTH_ZERO,
            attackerPlayerId: 1, eliminatedArea: AreaType.S2Library1);

        var result = Assert.Single(manager.BuildGameResult(), row => row.playerId == 2);

        Assert.Equal(1, result.attackerPlayerId);
        Assert.Equal(AreaType.S2Library1, result.eliminatedArea);
        Assert.Equal(EliminationReason.HEALTH_ZERO, result.reason);
    }
    [Fact]
    public void Elimination_FixesRankTierAndEnvironmentalCauseAtEliminationTime()
    {
        const long matchingId = 194003;
        var manager = MatchTestServices.Runtime(matchingId, NullLogger.Instance);

        manager.RegisterParticipant(CreateLink(1, 2));
        manager.RegisterParticipant(CreateLink(2, 3));
        manager.RegisterParticipant(CreateLink(3, 1));

        manager.TryEliminatePlayer(
            2,
            EliminationReason.PRESSURE_FIELD,
            forcedRank: 3,
            finalOrbTier: 2);

        var result = Assert.Single(manager.BuildGameResult(), row => row.playerId == 2);
        Assert.Equal(3, result.eliminationRank);
        Assert.Equal(2, result.finalOrbTier);
        Assert.Equal(EliminationReason.PRESSURE_FIELD, result.reason);
    }
    [Fact]
    public void Elimination_AppliesOnlyOnceAndPreservesTheFirstResult()
    {
        const long matchingId = 194004;
        var manager = MatchTestServices.Runtime(matchingId, NullLogger.Instance);

        manager.RegisterParticipant(CreateLink(1, 2));
        manager.RegisterParticipant(CreateLink(2, 3));
        manager.RegisterParticipant(CreateLink(3, 1));

        var first = manager.TryEliminatePlayer(
            2, EliminationReason.HEALTH_ZERO,
            attackerPlayerId: 1, forcedRank: 3, finalOrbTier: 2);
        var duplicate = manager.TryEliminatePlayer(
            2, EliminationReason.DETECTED,
            attackerPlayerId: 3, forcedRank: 2, finalOrbTier: 3);

        Assert.True(first);
        Assert.False(duplicate);

        var result = Assert.Single(manager.BuildGameResult(), row => row.playerId == 2);
        Assert.Equal(EliminationReason.HEALTH_ZERO, result.reason);
        Assert.Equal(1, result.attackerPlayerId);
        Assert.Equal(3, result.eliminationRank);
        Assert.Equal(2, result.finalOrbTier);
    }

    // #227 1단계: 본체 HP 0과 전투·자기장가 같은 틱에 겹쳐도 탈락은 한 번만 확정되어야 한다.
    // 중복이 통과하면 생존 수가 여러 번 줄어 뒤 사람의 등수가 밀리고, 절단 보상과 탈락 드롭이
    // 겹쳐 지급된다. 등수는 남은 생존 수에서 나오므로 다음 탈락자의 등수가 그 증거다.
    [Fact]
    public void Elimination_SameTickCollision_DecrementsAliveCountOnce()
    {
        const long matchingId = 227001;
        var manager = MatchTestServices.Runtime(matchingId, NullLogger.Instance);

        manager.RegisterParticipant(CreateLink(1, 2));
        manager.RegisterParticipant(CreateLink(2, 3));
        manager.RegisterParticipant(CreateLink(3, 1));

        // 본체 HP 0 — 첫 확정.
        var byBodyHp = manager.TryEliminatePlayer(
            2, EliminationReason.HEALTH_ZERO, attackerPlayerId: 1);
        // 같은 틱의 전투·자기장 정산이 같은 사람을 다시 밀어 넣는다.
        var byPressureField = manager.TryEliminatePlayer(
            2, EliminationReason.PRESSURE_FIELD);
        var byDuplicate = manager.TryEliminatePlayer(
            2, EliminationReason.HEALTH_ZERO);

        Assert.True(byBodyHp);
        Assert.False(byPressureField);
        Assert.False(byDuplicate);

        // 생존 수가 한 번만 줄었다면 다음 탈락자의 등수는 2다 — 세 번 줄었으면 0으로 밀린다.
        Assert.True(manager.TryEliminatePlayer(3, EliminationReason.HEALTH_ZERO));

        var results = manager.BuildGameResult();
        var second = Assert.Single(results, row => row.playerId == 2);
        Assert.Equal(3, second.eliminationRank);
        // 첫 확정의 맥락(공격자)이 뒤 호출에 덮이지 않아야 전리품 정산도 한 번으로 남는다.
        Assert.Equal(1, second.attackerPlayerId);
        Assert.Equal(2, Assert.Single(results, row => row.playerId == 3).eliminationRank);
    }

    [Fact]
    public void EndMatch_ClearsParticipantsAndRejectsFurtherRegistration()
    {
        var roster = MatchTestServices.Runtime(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        roster.RegisterParticipant(new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 10, Name = "player", WearItemIdList = [1] } });

        using (roster.Enter())
            roster.TryMarkEnded();
        using (roster.Enter())
            roster.TryMarkEnded();

        Assert.DoesNotContain(roster.BuildGameResult(), row => row.playerId == 10);
        Assert.Null(roster.GetParticipant(10)?.Profile);
        Assert.Empty(roster.BuildGameResult());
        Assert.Equal((false, (long?)null), roster.CheckGameOver());
        Assert.False(roster.TryEliminatePlayer(10, EliminationReason.HEALTH_ZERO));
        Assert.Throws<InvalidOperationException>(() =>
            roster.RegisterParticipant(new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 20 } }));
    }
    [Fact]
    public void Participant_OwnsTheProfileUsedByPacketsAndResults()
    {
        var roster = MatchTestServices.Runtime(1, NullLogger.Instance);
        var profile = new network.common.data.models.PlayerInfo
        {
            PlayerId = 10, Name = "player", WearItemIdList = [123]
        };
        var participant = new Player { Profile = profile };
        roster.RegisterParticipant(participant);

        Assert.Same(participant, roster.GetParticipant(10));
        var profiles = roster.GetPlayerProfiles();
        Assert.Same(profile, Assert.Single(profiles));
        profiles.Clear();
        Assert.Same(profile, Assert.Single(roster.GetPlayerProfiles()));

        Assert.True(roster.TryEliminatePlayer(10, EliminationReason.HEALTH_ZERO));
        Assert.Equal(PlayerMatchStatus.ELIMINATED, participant.Status);
        Assert.Same(profile, roster.GetParticipant(10)!.Profile);
        Assert.Equal(123, Assert.Single(profile.WearItemIdList));
        using (roster.Enter())
            roster.TryMarkEnded();
        Assert.Empty(roster.GetPlayerProfiles());
    }
    [Fact]
    public void EndMarked_PreservesResultsUntilOutermostMatchScopeExits()
    {
        var match = MatchTestServices.Runtime(194010, NullLogger.Instance);
        var participant = CreateLink(10, 0);
        match.RegisterParticipant(participant);
        match.TryEliminatePlayer(10, EliminationReason.PRESSURE_FIELD);

        using (match.Enter())
        {
            Assert.True(match.TryMarkEnded());
            using (match.Enter())
                Assert.Same(participant, match.GetParticipant(10));
            Assert.Equal(EliminationReason.PRESSURE_FIELD, Assert.Single(match.BuildGameResult()).reason);
            Assert.Single(match.GetPlayerProfiles());
        }

        Assert.Null(match.GetParticipant(10));
        Assert.Empty(match.BuildGameResult());
        Assert.Empty(match.GetPlayerProfiles());
    }

    private static Player CreateLink(long playerId, long _) => new()
    {
        Profile = new network.common.data.models.PlayerInfo { PlayerId = playerId }
    };
}
