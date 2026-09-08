using game_server.matches.combat;
using game_server.matches.entry;
using game_server.matches.field;
using game_server.matches.results;
using game_server.players;
using game_server.matches;
using System.Collections.Concurrent;
using System.Reflection;
using game_server.network;
using game_server.services;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.helpers;
using network.packets;

namespace demo_regression_tests;

public sealed class GameClientSessionPublicationTests
{
    public GameClientSessionPublicationTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(3)]
    public void HealthNotificationPublishesResultWithoutApplyingRecoveryAgain(int missingHealth)
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.SetHealth(session, Config.MAX_HEALTH - missingHealth);
        int expectedHealth = Config.MAX_HEALTH - missingHealth + Math.Min(10, missingHealth);
        using (session.Match.Enter())
        {
            var change = session.Condition.Recover(10);
            Assert.Equal(expectedHealth, session.CurrentHealth);
            Assert.Equal(Math.Min(10, missingHealth), change.Recovered);
            session.HealthChanges.Handle(change);
        }
        Assert.Equal(expectedHealth, session.CurrentHealth);
        var packet = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_PLAYER_STATS_UPDATE>(Protocol.G_TO_C_PLAYER_STATS_UPDATE);
        Assert.Equal(expectedHealth, packet.Health);
        Assert.Equal(10, packet.HealthDelta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerJoinHoldsMatchLockAndReleasesItAfterSend(bool sendThrows)
    {
        using var fixture = new SessionFixture();
        var joining = fixture.CreateSession(70001, 101, (AreaType)50);
        var existing = fixture.CreateSession(70001, 102, (AreaType)50);
        var otherArea = fixture.CreateSession(70001, 103, (AreaType)51);
        var runtime = joining.Match;
        int sendCount = 0;
        foreach (var session in new[] { joining, existing })
        {
            fixture.ConnectionFor(session).BeforeSend = protocol =>
            {
                Assert.Equal(Protocol.G_TO_C_OBJECT_INFO, protocol);
                Assert.True(Monitor.IsEntered(runtime.MatchLock));
                sendCount++;
            };
        }
        if (sendThrows)
            fixture.ConnectionFor(joining).ThrowOnceOn = Protocol.G_TO_C_OBJECT_INFO;
        var broadcast = typeof(GameClientSession).GetMethod(
            "SyncPlayersOnEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (sendThrows)
            Assert.IsType<InvalidOperationException>(
                Assert.Throws<TargetInvocationException>(() => broadcast.Invoke(joining, null)).InnerException);
        else
            broadcast.Invoke(joining, null);

        Assert.Equal(sendThrows ? 1 : 2, sendCount);
        Assert.False(Monitor.IsEntered(runtime.MatchLock));
        Assert.Empty(fixture.ConnectionFor(otherArea).AttemptedProtocols);
    }

    [Fact]
    public void PlayerJoinDoesNotPublishAfterMatchEnds()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.MarkTerminal(70001);
        var broadcast = typeof(GameClientSession).GetMethod(
            "SyncPlayersOnEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        broadcast.Invoke(session, null);
        Assert.Empty(fixture.ConnectionFor(session).AttemptedProtocols);
    }
    [Fact]
    public void CombatHit_UsesConfirmedHealthAndExplicitDotFlag()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 102, (AreaType)50);
        using (session.Match.Enter())
        {
            var combat = new MatchCombatDamageService(fixture.Store, fixture.EventLog, NullLogger<MatchCombatDamageService>.Instance);
            combat.ApplyProximityAutoCombatHit(session, 101, (AreaType)50, 123, 5, isPeriodicDamage: true, sourceHealth: 73);
        }
        var hit = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_COMBAT_HIT>(Protocol.G_TO_C_COMBAT_HIT);
        Assert.Equal(101, hit.AttackerId);
        Assert.Equal(102, hit.TargetId);
        Assert.Equal(73, hit.AttackerHealth);
        Assert.Equal(session.CurrentHealth, hit.TargetHealth);
        Assert.Equal(5, hit.Damage);
        Assert.Equal(123, hit.WeaponItemId);
        Assert.True(hit.IsDot);
    }

    [Fact]
    public void MonsterFeedback_DoesNotPackIdentityOrFlagsIntoHealth()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var combat = new MatchCombatDamageService(fixture.Store, fixture.EventLog, NullLogger<MatchCombatDamageService>.Instance);
        combat.SendMonsterHitNotification(session, 42, (AreaType)50, 123, 9, critical: true, showDamageOnly: true);
        var hit = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_COMBAT_HIT>(Protocol.G_TO_C_COMBAT_HIT);
        Assert.Equal(CombatEntityKind.Monster, hit.TargetKind);
        Assert.Equal(42, hit.TargetId);
        Assert.Equal(-1, hit.TargetHealth);
        Assert.True(hit.IsCritical);
        Assert.True(hit.ShowDamageOnly);
    }

    [Fact]
    public void MonsterHit_UsesOneDamageValueForHealthAndFeedback()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        int healthBefore = session.CurrentHealth;
        using (session.Match.Enter())
        {
            var combat = new MatchCombatDamageService(fixture.Store, fixture.EventLog, NullLogger<MatchCombatDamageService>.Instance);
            combat.ApplySwarmAfterimageMonsterHit(session, 42, 1);
        }
        var hit = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_COMBAT_HIT>(Protocol.G_TO_C_COMBAT_HIT);
        Assert.Equal(CombatEntityKind.Monster, hit.AttackerKind);
        Assert.Equal(42, hit.AttackerId);
        Assert.Equal(101, hit.TargetId);
        Assert.Equal(1, hit.Damage);
        Assert.Equal(healthBefore - 1, hit.TargetHealth);
    }

    [Fact]
    public void PlayerHitNotification_UsesProvidedTargetHealthWithoutLookup()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var combat = new MatchCombatDamageService(fixture.Store, fixture.EventLog, NullLogger<MatchCombatDamageService>.Instance);
        combat.SendPlayerHitNotification(session, 999, (AreaType)50, 123, 7, targetHealth: 61);
        var hit = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_COMBAT_HIT>(Protocol.G_TO_C_COMBAT_HIT);
        Assert.Equal(999, hit.TargetId);
        Assert.Equal(61, hit.TargetHealth);
    }

    [Fact]
    public void RecoveryAndStatusEffect_HaveSeparateTypedPackets()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.SetHealth(session, Config.MAX_HEALTH - 3);
        var recoveryService = new OrbRecoveryService(
            fixture.Store, fixture.EventLog, NullLogger<OrbRecoveryService>.Instance);
        var actor = new ProximityCombatActor(101, (AreaType)50, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (session.Match.Enter())
        {
            recoveryService.Process(70001, [actor], [session], [], now);
            recoveryService.Process(70001, [actor], [session], [], now.AddSeconds(OrbData.RecoveryTickSeconds));
        }
        using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
        {
            SourcePlayerId = 102,
            TargetPlayerId = 101,
            AreaType = (AreaType)50,
            Effect = CombatStatusEffectKind.WaveOrbSlow,
            DurationMs = 1500
        });
        Assert.True(session.TrySend(packet));
        var recovery = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_HEALTH_RECOVERY>(Protocol.G_TO_C_HEALTH_RECOVERY);
        Assert.Equal(HealthRecoveryKind.Orb, recovery.Source);
        Assert.Equal(actor.WeaponItemId, recovery.OrbItemId);
        Assert.Equal(Math.Min(3, OrbData.GetRecoveryAmount(actor.WeaponItemId)), recovery.Amount);
        var effect = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_STATUS_EFFECT>(Protocol.G_TO_C_STATUS_EFFECT);
        Assert.Equal(102, effect.SourcePlayerId);
        Assert.Equal(101, effect.TargetPlayerId);
        Assert.Equal(CombatStatusEffectKind.WaveOrbSlow, effect.Effect);
        Assert.Equal(1500, effect.DurationMs);
    }

    [Fact]
    public void SleepRecoverySendsRecoveryNotificationFromTick()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.SetHealth(session, Config.MAX_HEALTH - 1);
        var now = DateTime.UtcNow;
        using (session.Match.Enter())
        {
            Assert.True(session.Condition.TryStartSleep(now));
            MatchArenaService.ProcessSwarmSleepRecovery([session], now);
            MatchArenaService.ProcessSwarmSleepRecovery([session], now.AddSeconds(1));
        }
        var packet = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_HEALTH_RECOVERY>(Protocol.G_TO_C_HEALTH_RECOVERY);
        Assert.Equal(101, packet.PlayerId);
        Assert.Equal(HealthRecoveryKind.Sleep, packet.Source);
        Assert.Equal(1, packet.Amount);
        Assert.Equal(Config.MAX_HEALTH, session.CurrentHealth);
    }

    [Fact]
    public void SessionRegistration_ReturnsPreviousWithoutClosingIt_AndUpdatesMatchMembership()
    {
        using var fixture = new SessionFixture();
        var previous = fixture.CreateSession(70001, 101, (AreaType)50);
        var replacement = fixture.CreateSession(70002, 101, (AreaType)50);
        var registry = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);

        Assert.Null(registry.Register(101, previous));
        Assert.Null(registry.Register(101, previous));
        Assert.Same(previous, Assert.Single(fixture.Store.GetOrThrow(70001).Sessions.Snapshot()));

        Assert.Same(previous, registry.Register(101, replacement));

        Assert.False(fixture.ConnectionFor(previous).IsReleased);
        Assert.True(registry.TryGetSession(101, out var current));
        Assert.Same(replacement, current);
        Assert.Empty(fixture.Store.GetOrThrow(70001).Sessions.Snapshot());
        Assert.Same(replacement, Assert.Single(fixture.Store.GetOrThrow(70002).Sessions.Snapshot()));
        Assert.False(registry.Remove(previous));
        Assert.Same(replacement, Assert.Single(fixture.Store.GetOrThrow(70002).Sessions.Snapshot()));
    }

    [Fact]
    public void MatchSessions_FilterAreaAndExcludeEliminatedPlayers()
    {
        using var fixture = new SessionFixture();
        var self = fixture.CreateSession(70001, 101, (AreaType)50);
        var peer = fixture.CreateSession(70001, 102, (AreaType)50);
        var eliminated = fixture.CreateSession(70001, 103, (AreaType)50);
        eliminated.ApplyMatchStatus(PlayerMatchStatus.ELIMINATED);
        fixture.CreateSession(70001, 104, (AreaType)51);
        fixture.CreateSession(70002, 105, (AreaType)50);

        Assert.Equal(4, self.Match.Sessions.Snapshot().Count);
        Assert.Equal(2, self.Match.Sessions.GetInArea((AreaType)50).Count);
        Assert.Same(peer, Assert.Single(self.Match.Sessions.GetInArea((AreaType)50, self.PlayerId)));
    }

    [Fact]
    public void TerminalMatchClearsMembershipButKeepsGlobalConnectionUntilRemoved()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var runtime = session.Match;
        var registry = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        registry.Register(101, session);

        fixture.MarkTerminal(70001);

        Assert.Empty(runtime.Sessions.Snapshot());
        Assert.False(runtime.Sessions.HasSessions);
        Assert.Same(session, Assert.Single(registry.GetAllSessions()));
        Assert.Throws<InvalidOperationException>(() => registry.Register(101, session));
        Assert.Throws<InvalidOperationException>(() => runtime.Sessions.Add(101, session));
        Assert.True(registry.Remove(session));
        Assert.Empty(registry.GetAllSessions());
    }

    [Fact]
    public async Task OppositeMatchReplacementsDoNotAcquireEachOthersMatchLock()
    {
        using var fixture = new SessionFixture();
        var first = fixture.CreateSession(70001, 101, (AreaType)50);
        var second = fixture.CreateSession(70002, 202, (AreaType)50);
        var firstReplacement = fixture.CreateSession(70002, 101, (AreaType)50);
        var secondReplacement = fixture.CreateSession(70001, 202, (AreaType)50);
        firstReplacement.Match.Sessions.Remove(101, firstReplacement);
        secondReplacement.Match.Sessions.Remove(202, secondReplacement);
        var registry = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        registry.Register(101, first);
        registry.Register(202, second);
        using var ready = new Barrier(2);

        Task Replace(GameClientSession replacement) => Task.Run(() =>
        {
            using var scope = replacement.Match.Enter();
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
            registry.Register(replacement.PlayerId!.Value, replacement);
        });
        await Task.WhenAll(Replace(firstReplacement), Replace(secondReplacement)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(secondReplacement, Assert.Single(first.Match.Sessions.Snapshot()));
        Assert.Same(firstReplacement, Assert.Single(second.Match.Sessions.Snapshot()));
        Assert.False(registry.Remove(first));
        Assert.False(registry.Remove(second));
        Assert.Equal(2, registry.GetAllSessions().Count);
    }

    [Fact]
    public async Task RegistrationRacingTerminalCleanupCannotRepopulateMatch()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var runtime = session.Match;
        runtime.Sessions.Remove(101, session);
        var registry = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        using var ready = new Barrier(2);
        var register = Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
            try { registry.Register(101, session); }
            catch (InvalidOperationException) { Assert.True(runtime.IsEnded); }
        });
        var terminate = Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
            fixture.MarkTerminal(70001);
        });
        await Task.WhenAll(register, terminate).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(runtime.Sessions.Snapshot());
        Assert.Null(fixture.Store.GetOrNull(70001));
        registry.Remove(session);
        Assert.Empty(registry.GetAllSessions());
    }

    [Fact]
    public void SessionLeave_NotifiesOnlySameMatchAndArea_AndCleansLastHumanMatch()
    {
        using var fixture = new SessionFixture();
        var registry = new GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<GameSessionRegistry>.Instance);
        var leaving = fixture.CreateSession(70001, 101, (AreaType)50, registry.Remove);
        var nearby = fixture.CreateSession(70001, 102, (AreaType)50, registry.Remove);
        var otherArea = fixture.CreateSession(70001, 103, (AreaType)51, registry.Remove);
        var otherMatch = fixture.CreateSession(70002, 104, (AreaType)50, registry.Remove);
        foreach (var session in new[] { leaving, nearby, otherArea, otherMatch })
            registry.Register(session.PlayerId!.Value, session);

        leaving.OnRemoved();

        Assert.False(registry.TryGetSession(101, out _));
        Assert.Equal([Protocol.G_TO_C_AREA_PLAYER_LEAVE], fixture.ConnectionFor(nearby).DeliveredProtocols);
        Assert.Empty(fixture.ConnectionFor(otherArea).DeliveredProtocols);
        Assert.Empty(fixture.ConnectionFor(otherMatch).DeliveredProtocols);
        Assert.NotNull(fixture.Store.GetOrNull(70001));

        nearby.OnRemoved();
        otherArea.OnRemoved();

        Assert.Null(fixture.Store.GetOrNull(70001));
        Assert.NotNull(fixture.Store.GetOrNull(70002));
    }

    [Fact]
    public void SessionLeave_PreviousSessionDoesNotRemoveReplacementOrNotifyPeers()
    {
        using var fixture = new SessionFixture();
        var registry = new GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<GameSessionRegistry>.Instance);
        var previous = fixture.CreateSession(70001, 101, (AreaType)50, registry.Remove);
        var replacement = fixture.CreateSession(70001, 101, (AreaType)50, registry.Remove);
        var peer = fixture.CreateSession(70001, 102, (AreaType)50, registry.Remove);
        registry.Register(101, previous);
        registry.Register(101, replacement);
        registry.Register(102, peer);

        previous.OnRemoved();

        Assert.True(registry.TryGetSession(101, out var current));
        Assert.Same(replacement, current);
        Assert.Empty(fixture.ConnectionFor(peer).DeliveredProtocols);
        Assert.NotNull(fixture.Store.GetOrNull(70001));
    }

    [Fact]
    public async Task Door_StartAndFinish_RejectEarlyFinishThenPublishAfterElapsedTime()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(
            matchingId: 70001,
            playerId: 101,
            area: (AreaType)50);

        MatchStartGate.RegisterBotOnlyMatch(70001); // 테스트에서만 카운트다운을 생략한다.

        await SendAsync(
            session,
            Protocol.C_TO_G_DOOR_OPEN_START,
            new C_TO_G_DOOR_OPEN_START { InteractId = 702000101 });

        G_TO_C_DOOR_OPEN_ACK startAck = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK);
        Assert.Equal(ErrorCode.SUCCESS, startAck.ErrorCode);
        Assert.False(startAck.Completed);

        fixture.ConnectionFor(session).ClearPackets();
        await SendAsync(session, Protocol.C_TO_G_DOOR_OPEN_FINISH,
            new C_TO_G_DOOR_OPEN_FINISH { InteractId = 702000101 });
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK).ErrorCode);
        Assert.False(session.Match.Doors.IsDoorOpen(201));

        // 실제로 잠들지 않고 서버가 기록한 시작 시각만 앞당긴다.
        var interactions = (PlayerInteractionState)typeof(GameClientSession).GetField(
            "_interactions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        interactions.BeginDoor(702000101, Environment.TickCount64 - 3000);

        fixture.ConnectionFor(session).ClearPackets();
        await SendAsync(
            session,
            Protocol.C_TO_G_DOOR_OPEN_FINISH,
            new C_TO_G_DOOR_OPEN_FINISH { InteractId = 702000101 });

        Assert.Equal(
            [
                Protocol.G_TO_C_DOOR_STATE_UPDATE,
                Protocol.G_TO_C_DOOR_OPEN_ACK,
                Protocol.G_TO_C_PLAYER_STATE
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.True((fixture.Store.GetOrNull(70001)?.Doors.IsDoorOpen(201) == true));
        Assert.True(fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK).Completed);
        Assert.False(Monitor.IsEntered(fixture.Store.GetOrNull(70001)!.MatchLock));
    }

    [Theory]
    [InlineData(Config.SUMMON_STONE_GROUND_ITEM_ID, Protocol.G_TO_C_SUMMON_STONE_STATE)]
    [InlineData(Config.BOOTS_GROUND_ITEM_ID, null)]
    [InlineData(107000010, Protocol.G_TO_C_ORB_UPDATE)]
    public async Task GroundPickup_SuccessBranches_PreserveSubtypePrefixAndCommonSuffix(
        int itemId,
        Protocol? expectedPrefix)
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, itemId);
        int stonesBefore = fixture.Store.GetOrThrow(70001).SummonStones.GetSnapshot(101).StoneCount;

        await RunPickupTickAsync(session, fixture.EventLog);

        IReadOnlyList<Protocol> protocols = fixture.ConnectionFor(session).DeliveredProtocols;
        if (expectedPrefix.HasValue)
            Assert.Equal(expectedPrefix.Value, protocols[0]);
        Assert.Equal(Protocol.G_TO_C_GROUND_ITEM_REMOVED, protocols[^2]);
        Assert.Equal(Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT, protocols[^1]);
        Assert.Null(fixture.Store.GetOrThrow(70001).GroundItems.GetItem(item.GroundItemUid));

        if (itemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
            Assert.Equal(stonesBefore + 1, fixture.Store.GetOrThrow(70001).SummonStones.GetSnapshot(101).StoneCount);
        else if (itemId == 107000010)
            Assert.Contains(
                fixture.Store.GetOrThrow(70001).Inventory.GetAllItems(101),
                inventoryItem => inventoryItem.ItemId == itemId);
    }

    [Fact]
    public async Task GroundPickup_HeartAutoUse_CommitsStatsBeforeCommonSuffix()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetHealth(session, 20);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.HEART_GROUND_ITEM_ID);

        await RunPickupTickAsync(session, fixture.EventLog);

        Assert.Equal(
            [
                Protocol.G_TO_C_PLAYER_STATS_UPDATE,
                Protocol.G_TO_C_GROUND_ITEM_REMOVED,
                Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.True(session.CurrentHealth > 20);
    }

    [Theory]
    [InlineData(Config.HEART_GROUND_ITEM_ID)]
    [InlineData(Config.JAM_GROUND_ITEM_ID)]
    public async Task GroundPickup_Rejection_LeavesItemWithoutRepeatedFailurePackets(int itemId)
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, itemId);

        await RunPickupTickAsync(session, fixture.EventLog);

        await RunPickupTickAsync(session, fixture.EventLog);
        Assert.Empty(fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.NotNull(fixture.Store.GetOrThrow(70001).GroundItems.GetItem(item.GroundItemUid));
        Assert.DoesNotContain(fixture.Store.GetOrThrow(70001).Inventory.GetAllItems(101),
            inventoryItem => inventoryItem.ItemId == itemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoorOpen_UnavailableMatchSendsFailureAndReleasesLock(bool terminal)
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var runtime = session.Match;
        if (terminal)
            fixture.MarkTerminal(70001);
        else
            fixture.SetMatchingId(session, 0);

        await SendAsync(session, Protocol.C_TO_G_DOOR_OPEN_START,
            new C_TO_G_DOOR_OPEN_START { InteractId = 201 });

        var result = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK);
        Assert.Equal(201, result.InteractId);
        Assert.False(result.Completed);
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, result.ErrorCode);
        Assert.False(Monitor.IsEntered(runtime.MatchLock));
    }

    [Fact]
    public async Task NoMatchingRuntime_PreservesProtocolSpecificRejections()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetMatchingId(session, 0);

        await SendAsync(
            session,
            Protocol.C_TO_G_DOOR_OPEN_START,
            new C_TO_G_DOOR_OPEN_START { InteractId = 702000101 });
        await SendAsync(
            session,
            Protocol.C_TO_G_DOOR_OPEN_FINISH,
            new C_TO_G_DOOR_OPEN_FINISH { InteractId = 702000101 });

        Assert.Equal(
            [
                Protocol.G_TO_C_DOOR_OPEN_ACK,
                Protocol.G_TO_C_DOOR_OPEN_ACK
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_ERROR, fixture.ConnectionFor(session).AttemptedProtocols);
        foreach (G_TO_C_DOOR_OPEN_ACK ack in fixture.ConnectionFor(session)
                     .DeserializeAll<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK))
        {
            Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        }
    }

    [Fact]
    public async Task TerminalWhileWaitingForLock_PublishesRejectionWithoutMutation()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)50);
        MatchRuntime runtime = fixture.Store.GetOrNull(70001)!;
        using var lockHeld = new ManualResetEventSlim();
        using var markTerminal = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using (MatchRuntimeStore.Enter(runtime))
            {
                lockHeld.Set();
                Assert.True(markTerminal.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(runtime.TryMarkEnded());
            }
        });
        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));

        // 잠금을 기다리는 사이 매치가 끝나면 core 대신 거부 응답만 나간다.
        Task message = Task.Run(() => SendAsync(
            session,
            Protocol.C_TO_G_DOOR_OPEN_START,
            new C_TO_G_DOOR_OPEN_START { InteractId = 702000101 }));
        await Task.Delay(100);
        Assert.False(message.IsCompleted);
        markTerminal.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await message.WaitAsync(TimeSpan.FromSeconds(5));

        G_TO_C_DOOR_OPEN_ACK ack = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK);
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        Assert.Equal([Protocol.G_TO_C_DOOR_OPEN_ACK], fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.False((fixture.Store.GetOrNull(70001)?.Doors.IsDoorOpen(201) == true));
        Assert.Null(fixture.Store.GetOrNull(70001));
    }

    [Fact]
    public async Task TerminalMatch_RejectsLateDoorRequestWithoutRecreatingRuntime()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.MarkTerminal(70001);

        await SendAsync(
            session,
            Protocol.C_TO_G_DOOR_OPEN_START,
            new C_TO_G_DOOR_OPEN_START { InteractId = 702000101 });

        Assert.Equal([Protocol.G_TO_C_DOOR_OPEN_ACK], fixture.ConnectionFor(session).AttemptedProtocols);
        var ack = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK);
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        Assert.Equal(702000101, ack.InteractId);
        Assert.Null(fixture.Store.GetOrNull(70001));
    }

    [Fact]
    public async Task TerminalDuringBlockedHandler_WaitsForWholeBundleThenCleansUp()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.SUMMON_STONE_GROUND_ITEM_ID);
        var enteredDispatch = new ManualResetEventSlim();
        var releaseDispatch = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        fixture.CleanupTimeline = timeline;
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        connection.BeforeSend = protocol =>
        {
            timeline.Enqueue($"send:{protocol}");
            if (protocol != Protocol.G_TO_C_SUMMON_STONE_STATE)
                return;
            enteredDispatch.Set();
            Assert.True(releaseDispatch.Wait(TimeSpan.FromSeconds(5)));
        };

        Task message = Task.Run(() => RunPickupTickAsync(session, fixture.EventLog));
        Assert.True(enteredDispatch.Wait(TimeSpan.FromSeconds(5)));

        // 핸들러가 잠금을 쥔 채 송신 중이면 종료는 번들 전체가 끝날 때까지 기다린다.
        Assert.False(fixture.Store.TryEnter(70001, out _));
        Task terminal = Task.Run(() =>
        {
            fixture.MarkTerminal(70001);
            timeline.Enqueue("after");
        });
        await Task.Delay(100);
        Assert.False(terminal.IsCompleted);
        Assert.DoesNotContain("cleanup", timeline);

        releaseDispatch.Set();
        await message.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "send:G_TO_C_SUMMON_STONE_STATE",
                "send:G_TO_C_GROUND_ITEM_REMOVED",
                "send:G_TO_C_GROUND_ITEM_PICKUP_RESULT",
                "cleanup",
                "after"
            ],
            timeline);
        Assert.Null(fixture.Store.GetOrNull(70001));
    }

    [Fact]
    public async Task TransportFailure_DoesNotRollbackStateAndReleasesMatchLock()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.SUMMON_STONE_GROUND_ITEM_ID);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        connection.ThrowOnceOn = Protocol.G_TO_C_GROUND_ITEM_REMOVED;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunPickupTickAsync(session, fixture.EventLog));

        Assert.Equal(1, session.Match.SummonStones.GetSnapshot(session.PlayerId!.Value).StoneCount);
        Assert.Null(fixture.Store.GetOrThrow(70001).GroundItems.GetItem(item.GroundItemUid));
        Assert.Equal(
            [
                Protocol.G_TO_C_SUMMON_STONE_STATE,
                Protocol.G_TO_C_GROUND_ITEM_REMOVED
            ],
            connection.AttemptedProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT, connection.AttemptedProtocols);
        Assert.False(Monitor.IsEntered(fixture.Store.GetOrNull(70001)!.MatchLock));
    }

    [Fact]
    public async Task PreparationFailure_KeepsAlreadySentPrefixAndReleasesMatchLock()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetHealth(session, 20);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.HEART_GROUND_ITEM_ID);
        GameClientSession.SwarmHeartPickupCallback = static (_, _) =>
            throw new InvalidOperationException("heart callback failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunPickupTickAsync(session, fixture.EventLog));

        Assert.Equal(
            [Protocol.G_TO_C_PLAYER_STATS_UPDATE],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.Null(fixture.Store.GetOrThrow(70001).GroundItems.GetItem(item.GroundItemUid));
        Assert.True(session.CurrentHealth > 20);
    }

    [Fact]
    public async Task DifferentMatches_DispatchIndependentlyWhileOneTransportIsBlocked()
    {
        using var fixture = new SessionFixture();
        RecordingSession first = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        RecordingSession second = fixture.CreateSession(70002, 202, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo firstItem = fixture.SpawnAtSession(first, Config.SUMMON_STONE_GROUND_ITEM_ID);
        GroundItemInfo secondItem = fixture.SpawnAtSession(second, Config.SUMMON_STONE_GROUND_ITEM_ID);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        fixture.ConnectionFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_SUMMON_STONE_STATE)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task firstTask = Task.Run(() => RunPickupTickAsync(first, fixture.EventLog));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task secondTask = RunPickupTickAsync(second, fixture.EventLog);
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, second.Match.SummonStones.GetSnapshot(second.PlayerId!.Value).StoneCount);
        Assert.False(firstTask.IsCompleted);

        release.Set();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Match.SummonStones.GetSnapshot(first.PlayerId!.Value).StoneCount);
    }

    [Fact]
    public void SourceScope_ActivatesOnlySelectedPlayerOuterHandlers()
    {
        string root = FindRepositoryRoot();
        string session = ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.cs");
        Assert.False(File.Exists(Path.Combine(root, "game_server", "Sessions", "GameClientSession.RngCollect.cs")));
        Assert.False(File.Exists(Path.Combine(root, "game_server", "Sessions", "GameClientSession.GroundItem.cs")));
        string orbSummon = ReadNormalizedSource(
            root,
            "game_server",
            "Sessions",
            "GameClientSession.OrbSummon.cs");
        string doors = ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.Doors.cs");
        string connection = ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.cs");
        string arena = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchArenaService.cs");
        string bots = ReadNormalizedSource(root, "game_server", "Services", "Bots", "BotDecisionService.cs");
        string botPickup = ReadNormalizedSource(
            root,
            "game_server",
            "Services",
            "Bots",
            "BotPlayerManager.ProximityAutoCombat.cs");

        Assert.DoesNotContain("AsyncLocal", session);
        Assert.DoesNotContain("IsMessageLifecycleActive", session);
        Assert.DoesNotContain("IsMessageLifecycleActive",
            ReadNormalizedSource(root, "network", "Core", "SessionBase.cs"));
        Assert.DoesNotContain("RunWithMatchLock", session);
        Assert.DoesNotContain(Enum.GetNames<Protocol>(), name => name.Contains("RNG_COLLECT") || name.Contains("INTERACT_COOLDOWN"));
        var autoPickup = ReadNormalizedSource(root, "game_server", "Services", "GroundItemAutoPickupService.cs");
        Assert.Contains("Monitor.IsEntered(match.MatchLock)", autoPickup);
        Assert.Contains("match.IsEnded", autoPickup);
        Assert.DoesNotContain("RunWithMatchLock", orbSummon);
        Assert.Equal(2, CountOccurrences(orbSummon, "using (match.Enter())"));
        Assert.Equal(2, CountOccurrences(orbSummon, "if (match.IsEnded"));
        Assert.DoesNotContain("SwarmGrowthPickCallback", session);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", session);
        Assert.DoesNotContain("SwarmGrowthPickCallback", arena);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", arena);
        string playerState = ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.PlayerState.cs");
        Assert.DoesNotContain("RunWithMatchLock", playerState);
        Assert.DoesNotContain("ProcessPlayerState", playerState);
        Assert.DoesNotContain("OnPeriodicBuffTick", playerState);
        Assert.DoesNotContain("_periodicBuffTimer", session);
        Assert.DoesNotContain("new Timer(", playerState);
        Assert.Contains("ProcessPeriodicBuffs(matchingId, aliveSessions, nowUtc)", arena);
        Assert.Contains("session.Condition.UpdatePeriodicBuffs(nowUtc", arena);
        Assert.Equal(2, CountOccurrences(playerState, "MatchInteractionService.CancelPendingInteractions(match, _interactions)"));

        Assert.DoesNotContain("ProcessUseInGameItem", playerState);
        Assert.DoesNotContain("HandleUseInGameItem", playerState);
        Assert.DoesNotContain("HandleHealthChanged", playerState);
        Assert.DoesNotContain("SendInGameInventory", playerState);
        Assert.DoesNotContain("HandleUseInGameItem", session);
        Assert.False(Enum.GetNames<Protocol>().Contains("C_TO_G_USE_INGAME_ITEM"));
        Assert.False(Enum.GetNames<Protocol>().Contains("G_TO_C_USE_INGAME_ITEM_RESULT"));
        Assert.DoesNotContain("HandleRestStateRequest", playerState);
        Assert.DoesNotContain("await ", playerState);
        Assert.Equal(1, CountOccurrences(playerState, "using (match.Enter())"));
        Assert.Equal(1, CountOccurrences(playerState, "if (match.IsEnded"));
        Assert.DoesNotContain("RunWithMatchLock(", doors);
        Assert.Contains("using (match.Enter())", doors);
        Assert.DoesNotContain("ProcessDoorOpenRequest", doors);
        Assert.DoesNotContain("RunWithMatchLock(", ReadMethodSlice(connection,
            "private async Task HandleConnect(", "private void LogInitialInventory("));
        Assert.DoesNotContain("RunWithMatchLock", arena);
        Assert.DoesNotContain("RunWithMatchLock", bots);
        Assert.DoesNotContain("RunWithMatchLock", botPickup);
        Assert.Contains("session.BreakDoorUnlockGauge();", arena);

        Assert.DoesNotContain(
            "RunWithMatchLock",
            ReadMethodSlice(
                doors,
                "internal void BreakDoorUnlockGauge()",
                "private void SendDoorStateList("));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoorStateList_HoldsMatchLockDuringSendAndReleasesIt(bool sendThrows)
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var runtime = session.Match;
        var connection = fixture.ConnectionFor(session);
        bool sentUnderLock = false;
        connection.BeforeSend = protocol =>
        {
            if (protocol == Protocol.G_TO_C_DOOR_STATE_LIST)
                sentUnderLock = Monitor.IsEntered(runtime.MatchLock);
        };
        if (sendThrows)
            connection.ThrowOnceOn = Protocol.G_TO_C_DOOR_STATE_LIST;

        var send = typeof(GameClientSession).GetMethod(
            "SendDoorStateList", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (sendThrows)
            Assert.IsType<InvalidOperationException>(
                Assert.Throws<TargetInvocationException>(() => send.Invoke(session, null)).InnerException);
        else
            send.Invoke(session, null);

        Assert.True(sentUnderLock);
        Assert.False(Monitor.IsEntered(runtime.MatchLock));
    }

    [Fact]
    public void DoorHit_ReportsInterruptedAndInvalidatesPendingFinish()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        var interactions = (PlayerInteractionState)typeof(GameClientSession).GetField(
            "_interactions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        using (session.Match.Enter())
        {
            interactions.CompleteDoor(); // 첫 문 피격 면제 이후의 문을 검사한다.
            interactions.BeginDoor(702000101, 0);
            session.BreakDoorUnlockGauge();
            Assert.False(interactions.TryFinishDoor(702000101, 3000, TimeSpan.FromSeconds(3), out var error));
            Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
        }
        var ack = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK);
        Assert.Equal(ErrorCode.DOOR_OPEN_INTERRUPTED, ack.ErrorCode);
        Assert.False(ack.Completed);
    }

    [Fact]
    public async Task DoorFinishWithoutStartIsRejected()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        MatchStartGate.RegisterBotOnlyMatch(70001);
        await SendAsync(session, Protocol.C_TO_G_DOOR_OPEN_FINISH,
            new C_TO_G_DOOR_OPEN_FINISH { InteractId = 702000101 });
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK).ErrorCode);
        Assert.False(session.Match.Doors.IsDoorOpen(201));
    }

    [Fact]
    public async Task IdleCancelsDoorGaugeAndReportsDoorAckOnlyOnce()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, (AreaType)50);
        MatchStartGate.RegisterBotOnlyMatch(70001);
        var interactions = (PlayerInteractionState)typeof(GameClientSession).GetField(
            "_interactions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        using (session.Match.Enter())
            interactions.BeginDoor(702000101, 0);

        await SendAsync(session, Protocol.C_TO_G_PLAYER_STATE,
            new C_TO_G_PLAYER_STATE { State = PlayerState.IDLE });
        await SendAsync(session, Protocol.C_TO_G_PLAYER_STATE,
            new C_TO_G_PLAYER_STATE { State = PlayerState.IDLE });

        var ack = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_DOOR_OPEN_ACK>(Protocol.G_TO_C_DOOR_OPEN_ACK);
        Assert.Equal(702000101, ack.InteractId);
        Assert.Equal(ErrorCode.DOOR_OPEN_INTERRUPTED, ack.ErrorCode);
        Assert.False(ack.Completed);
        using (session.Match.Enter())
            Assert.False(interactions.TryFinishDoor(702000101, 3000, TimeSpan.FromSeconds(3), out _));
        Assert.False(session.Match.Doors.IsDoorOpen(201));
    }
    private static async Task SendAsync<T>(
        GameClientSession session,
        Protocol protocol,
        T message)
    {
        byte[] wireBytes;
        using (var packet = Packet.Create((int)protocol, session.PlayerId ?? 0))
        {
            packet.SetBody(MessagePackSerializer.Serialize(message));
            packet.RecordSize();
            wireBytes = packet.ToBytes();
        }

        await session.OnMessageFromClient(wireBytes);
    }

    private static string ReadNormalizedSource(string repositoryRoot, params string[] parts) =>
        File.ReadAllText(Path.Combine([repositoryRoot, .. parts]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }
        return count;
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    [Fact]
    public void MatchTick_AutomaticallyPicksUpForStationaryPlayerOnlyAfterGameplayStarts()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var item = fixture.SpawnAtSession(session, Config.SUMMON_STONE_GROUND_ITEM_ID);
        var runner = CreatePickupTickRunner(fixture);
        runner.Run(session.Match);
        Assert.NotNull(session.Match.GroundItems.GetItem(item.GroundItemUid));
        Assert.Empty(fixture.ConnectionFor(session).DeliveredProtocols);

        // 테스트에서 대기 없이 활성 게이트를 연다.
        MatchStartGate.RegisterBotOnlyMatch(session.MatchingId);
        runner.Run(session.Match);
        runner.Run(session.Match);
        Assert.Null(session.Match.GroundItems.GetItem(item.GroundItemUid));
        Assert.Equal(1, session.Match.SummonStones.GetSnapshot(session.PlayerId!.Value).StoneCount);
        Assert.Single(fixture.ConnectionFor(session).DeserializeAll<G_TO_C_GROUND_ITEM_PICKUP_RESULT>(
            Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT));
    }

    [Fact]
    public void MatchTick_TwoPlayersNearSameItemOnlyOneReceivesIt()
    {
        using var fixture = new SessionFixture();
        var first = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var second = fixture.CreateSession(70001, 102, Config.SWARM_MATCH_GROUND_AREA);
        var item = fixture.SpawnAtSession(first, Config.SUMMON_STONE_GROUND_ITEM_ID);
        TestGameSessionServices.SetMovementProperty(second, "LastValidatedPosition", first.LastValidatedPosition);
        MatchStartGate.RegisterBotOnlyMatch(first.MatchingId);

        CreatePickupTickRunner(fixture).Run(first.Match);

        Assert.Equal(1, first.Match.SummonStones.GetSnapshot(first.PlayerId!.Value).StoneCount + second.Match.SummonStones.GetSnapshot(second.PlayerId!.Value).StoneCount);
        Assert.Null(first.Match.GroundItems.GetItem(item.GroundItemUid));
    }

    [Fact]
    public void MatchTick_OnePlayerNotificationFailureDoesNotStopOtherPlayersPickup()
    {
        using var fixture = new SessionFixture();
        var first = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var second = fixture.CreateSession(70001, 102, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SpawnAtSession(first, Config.SUMMON_STONE_GROUND_ITEM_ID);
        fixture.SpawnAtSession(second, Config.SUMMON_STONE_GROUND_ITEM_ID);
        fixture.ConnectionFor(first).ThrowOnceOn = Protocol.G_TO_C_SUMMON_STONE_STATE;
        MatchStartGate.RegisterBotOnlyMatch(first.MatchingId);

        CreatePickupTickRunner(fixture).Run(first.Match);

        Assert.Equal(1, first.Match.SummonStones.GetSnapshot(first.PlayerId!.Value).StoneCount);
        Assert.Equal(1, second.Match.SummonStones.GetSnapshot(second.PlayerId!.Value).StoneCount);
        Assert.False(Monitor.IsEntered(first.Match.MatchLock));
    }

    [Fact]
    public void AutomaticPickupCandidatesAreMatchOwnedAndClearedOnTerminal()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SpawnAtSession(session, Config.SUMMON_STONE_GROUND_ITEM_ID);
        var match = session.Match;
        using (match.Enter())
        {
            GroundItemAutoPickupService.RecordMovement(session,
                session.LastValidatedPosition!, session.LastValidatedPosition!, session.CurrentArea);
            Assert.Single(match.GroundItemPickupCandidates);
        }

        fixture.MarkTerminal(session.MatchingId);
        Assert.Empty(match.GroundItemPickupCandidates);
    }

    [Fact]
    public void ReplacementSessionDoesNotInheritPreviousSessionPickupCandidates()
    {
        using var fixture = new SessionFixture();
        var previous = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var item = fixture.SpawnAtSession(previous, Config.SUMMON_STONE_GROUND_ITEM_ID);
        var match = previous.Match;
        using (match.Enter())
            GroundItemAutoPickupService.RecordMovement(previous,
                previous.LastValidatedPosition!, previous.LastValidatedPosition!, previous.CurrentArea);

        var current = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        TestGameSessionServices.SetMovementProperty(current, "LastValidatedPosition", new Vector3f(item.PositionX + 20, item.PositionY, 0));
        using (match.Enter())
            new GroundItemAutoPickupService(fixture.EventLog, NullLogger<GroundItemAutoPickupService>.Instance)
                .Process(match, [current]);

        Assert.Equal(0, current.Match.SummonStones.GetSnapshot(current.PlayerId!.Value).StoneCount);
        Assert.NotNull(match.GroundItems.GetItem(item.GroundItemUid));
        Assert.Empty(match.GroundItemPickupCandidates);
    }

    [Fact]
    public void GroundItemDropServiceClearsInventoryAndPublishesOnlyOnce()
    {
        using var fixture = new SessionFixture();
        var eliminated = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var observer = fixture.CreateSession(70001, 102, Config.SWARM_MATCH_GROUND_AREA);
        var match = eliminated.Match;
        using (match.Enter())
        {
            Assert.True(match.Inventory.TryAddItemWithCapacity(101, 107000010, Config.GetOrbCapacity(), out _));
            eliminated.ApplyMatchStatus(PlayerMatchStatus.ELIMINATED);
            var drops = new GroundItemDropService(fixture.EventLog);
            drops.DropAll(eliminated);
            drops.DropAll(eliminated);

            Assert.Empty(match.Inventory.GetAllItems(101));
            Assert.Single(match.GroundItems.GetSnapshot(eliminated.CurrentArea));
        }
        Assert.Equal([Protocol.G_TO_C_ORB_UPDATE], fixture.ConnectionFor(eliminated).DeliveredProtocols);
        Assert.Equal([Protocol.G_TO_C_GROUND_ITEM_SPAWN], fixture.ConnectionFor(observer).DeliveredProtocols);
    }

    [Fact]
    public void GroundItemSnapshotOnlyGoesToRequestedSession()
    {
        using var fixture = new SessionFixture();
        var owner = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var other = fixture.CreateSession(70001, 102, Config.SWARM_MATCH_GROUND_AREA);
        var item = fixture.SpawnAtSession(owner, Config.SUMMON_STONE_GROUND_ITEM_ID);
        using (owner.Match.Enter())
            GroundItemNotificationService.SendSnapshot(owner, owner.CurrentArea);
        var snapshot = fixture.ConnectionFor(owner).DeserializeSingle<G_TO_C_GROUND_ITEM_SNAPSHOT>(
            Protocol.G_TO_C_GROUND_ITEM_SNAPSHOT);
        Assert.Equal(item.GroundItemUid, Assert.Single(snapshot.Items).GroundItemUid);
        Assert.Empty(fixture.ConnectionFor(other).DeliveredProtocols);
    }

    [Fact]
    public void GroundItemSpawnOnlyGoesToLivePlayersInTheSameArea()
    {
        using var fixture = new SessionFixture();
        var owner = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        var otherArea = fixture.CreateSession(70001, 102, (AreaType)999);
        var eliminated = fixture.CreateSession(70001, 103, Config.SWARM_MATCH_GROUND_AREA);
        eliminated.ApplyMatchStatus(PlayerMatchStatus.ELIMINATED);
        var item = fixture.SpawnAtSession(owner, Config.SUMMON_STONE_GROUND_ITEM_ID);
        using (owner.Match.Enter())
        {
            GroundItemNotificationService.BroadcastSpawned(owner.Match, owner.CurrentArea, []);
            Assert.Empty(fixture.ConnectionFor(owner).DeliveredProtocols);
            GroundItemNotificationService.BroadcastSpawned(owner.Match, owner.CurrentArea, [item]);
        }
        Assert.Equal([Protocol.G_TO_C_GROUND_ITEM_SPAWN], fixture.ConnectionFor(owner).DeliveredProtocols);
        Assert.Empty(fixture.ConnectionFor(otherArea).DeliveredProtocols);
        Assert.Empty(fixture.ConnectionFor(eliminated).DeliveredProtocols);
    }

    private static MatchTickRunner CreatePickupTickRunner(SessionFixture fixture) =>
        new(fixture.Store, NullLogger.Instance,
            new GroundItemAutoPickupService(fixture.EventLog, NullLogger<GroundItemAutoPickupService>.Instance),
            static (_, _) => { }, static (_, _) => { },
            static (_, _) => { }, static _ => { }, static (_, _) => { });

    private static Task RunPickupTickAsync(GameClientSession session, GameEventLogManager eventLogs)
    {
        using (session.Match.Enter())
            new GroundItemAutoPickupService(eventLogs, NullLogger<GroundItemAutoPickupService>.Instance).Process(session);
        return Task.CompletedTask;
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly List<GameClientSession> _sessions = [];
        private readonly Dictionary<GameClientSession, RecordingTcpConnection> _connections = [];
        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-session-publication-tests",
            Guid.NewGuid().ToString("N"));

        public SessionFixture()
        {
            Store = TestGameSessionServices.CreateMatchRuntimeStore(
                NullLogger.Instance,
                onRedisCleanup: _ => CleanupTimeline?.Enqueue("cleanup"));

            GameClientSession.SwarmHeartPickupCallback = null;
        }

        public MatchRuntimeStore Store { get; }
        public ConcurrentQueue<string>? CleanupTimeline { get; set; }

        public GameEventLogManager EventLog { get; } = TestGameEventLogs.Create();
        public MatchSummaryFileStore Summaries => new(_summaryDirectory);

        public RecordingSession CreateSession(long matchingId, long playerId, AreaType area, Func<GameClientSession, bool>? removeSession = null)
        {
            Store.GetOrCreate(matchingId);
            Store.GetOrNull(matchingId)!.Doors.Initialize();

            var connection = new RecordingTcpConnection();
            Activate(connection);
            var session = new RecordingSession(
                connection,
                _sessions,

                EventLog,
                Summaries,
                Store, removeSession);
            SetIdentity(session, matchingId, playerId, area);
            _sessions.Add(session);
            session.Match.Sessions.Add(playerId, session);
            _connections.Add(session, connection);
            return session;
        }

        public RecordingTcpConnection ConnectionFor(GameClientSession session) => _connections[session];

        /// <summary>잠금 안에서 터미널로 표시하고 나온다 — 정리는 깊이 0 탈출에서 바로 돈다.</summary>
        public void MarkTerminal(long matchingId)
        {
            MatchRuntime runtime = Store.GetOrNull(matchingId)!;
            using (MatchRuntimeStore.Enter(runtime))
            {
                Assert.True(runtime.TryMarkEnded());
            }
        }

        public GroundItemInfo SpawnAtSession(RecordingSession session, int itemId)
        {
            GroundItemInfo item = Store.GetOrThrow(session.MatchingId).GroundItems.SpawnItems(
                session.CurrentArea,
                0f,
                0f,
                [itemId],
                mapId: Config.SWARM_MATCH_MAP).Single();
            SetPosition(session, new Vector3f(item.PositionX, item.PositionY, 0f));
            TestGroundItemLanding.Complete(session.Match.GroundItems);
            return item;
        }

        public void SetHealth(RecordingSession session, int health)
        {
            var condition = (PlayerCondition)typeof(GameClientSession).GetField(
                "_condition", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            condition.Health = health;
        }

        public void SetMatchingId(RecordingSession session, long matchingId)
        {
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
            TestGameSessionServices.BindMatch(session, matchingId);
        }

        public void SeedPendingFinish(RecordingSession session, int interactId)
        {
            var pending = Assert.IsType<PlayerInteractionState>(
                typeof(GameClientSession).GetField(
                    "_interactions",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
            pending.Begin(interactId);
        }

        public void Dispose()
        {
            GameClientSession.SwarmHeartPickupCallback = null;
            foreach (long matchingId in _sessions.Select(session => session.MatchingId).Distinct())
                MatchStartGate.RemoveMatching(matchingId);
            if (Directory.Exists(_summaryDirectory))
                Directory.Delete(_summaryDirectory, recursive: true);
        }

        private static void SetIdentity(
            GameClientSession session,
            long matchingId,
            long playerId,
            AreaType area)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
            TestGameSessionServices.BindMatch(session, matchingId);
            SetProperty(session, nameof(GameClientSession.CurrentMapId), Config.SWARM_MATCH_MAP);
            TestGameSessionServices.SetMovementProperty(session, "CurrentArea", area);
            SetPosition(session, new Vector3f(0f, 0f, 0f));
        }

        private static void SetProperty(GameClientSession session, string name, object value) =>
            typeof(GameClientSession).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(session, value);

        private static void SetPosition(GameClientSession session, Vector3f position) =>
            TestGameSessionServices.SetMovementProperty(session, "LastValidatedPosition", position);

        private static void Activate(TcpConnection connection)
        {
            int active = (int)typeof(TcpConnection).GetField(
                "StateActive",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            typeof(TcpConnection).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, active);
        }

    }

    private sealed class RecordingSession : GameClientSession
    {
        public RecordingSession(
            TcpConnection connection,
            List<GameClientSession> sessions,

            GameEventLogManager eventLog,
            MatchSummaryFileStore summaries,
            MatchRuntimeStore matchRuntimes, Func<GameClientSession, bool>? removeSession = null)
            : base(
                connection,
                NullLogger.Instance,
                null!,
                removeSession ?? (static _ => false),
                new MatchCleanupService(matchRuntimes, eventLog, summaries, NullLogger.Instance),
                static (_, _) => null,

                eventLog,
                TestGameSessionServices.CreateEliminationService(matchRuntimes, eventLog, summaries, GameServerDevOptions.Disabled, NullLogger.Instance),
                new FakePlayerGrowthHandler(),
                new FakeGameSessionLifecycle(),
                static () => false,
                new FakeMatchEntryFailureHandler(),
                TestGameSessionServices.CreateEntryService(null!, matchRuntimes, GameServerDevOptions.Disabled, NullLogger.Instance),
                new MovementValidationService(NullLogger<MovementValidationService>.Instance),
                orbInventory: new OrbInventoryService(eventLog))
        {
        }
    }

    private sealed class RecordingTcpConnection : TcpConnection
    {
        private readonly object _gate = new();
        private readonly List<(Protocol Protocol, byte[] WireBytes)> _delivered = [];
        private readonly List<Protocol> _attempted = [];
        private int _thrown;

        public Protocol? ThrowOnceOn { get; set; }
        public Action<Protocol>? BeforeSend { get; set; }

        public IReadOnlyList<Protocol> DeliveredProtocols
        {
            get
            {
                lock (_gate)
                    return _delivered.Select(entry => entry.Protocol).ToList();
            }
        }

        public IReadOnlyList<Protocol> AttemptedProtocols
        {
            get
            {
                lock (_gate)
                    return _attempted.ToList();
            }
        }

        public override bool TrySend(Packet msg)
        {
            msg.RecordSize();
            var protocol = (Protocol)msg.ProtocolId;
            lock (_gate)
                _attempted.Add(protocol);

            BeforeSend?.Invoke(protocol);
            if (ThrowOnceOn == protocol && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InvalidOperationException($"transport failed for {protocol}");

            byte[] wireBytes = msg.ToBytes();
            lock (_gate)
                _delivered.Add((protocol, wireBytes));
            return true;
        }

        public T DeserializeSingle<T>(Protocol protocol)
        {
            byte[] wireBytes;
            lock (_gate)
                wireBytes = Assert.Single(
                    _delivered,
                    entry => entry.Protocol == protocol).WireBytes;

            using var packet = Packet.Create(wireBytes);
            Assert.Equal((int)protocol, packet.PopProtocolId());
            _ = packet.PopPlayerId();
            return MessagePackSerializer.Deserialize<T>(packet.PopBody());
        }

        public IReadOnlyList<T> DeserializeAll<T>(Protocol protocol)
        {
            List<byte[]> wirePackets;
            lock (_gate)
            {
                wirePackets = _delivered
                    .Where(entry => entry.Protocol == protocol)
                    .Select(entry => entry.WireBytes)
                    .ToList();
            }

            return wirePackets.Select(wireBytes =>
            {
                using var packet = Packet.Create(wireBytes);
                Assert.Equal((int)protocol, packet.PopProtocolId());
                _ = packet.PopPlayerId();
                return MessagePackSerializer.Deserialize<T>(packet.PopBody());
            }).ToList();
        }

        public void ClearPackets()
        {
            lock (_gate)
            {
                _attempted.Clear();
                _delivered.Clear();
            }
            _thrown = 0;
            ThrowOnceOn = null;
            BeforeSend = null;
        }
    }
}
