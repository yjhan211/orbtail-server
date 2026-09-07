using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class OrbRecoveryServiceTests
{
    [Fact]
    public void Recovery_AggregatesDueOrbsAndRemovesMissingOrbClocks()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947301);
        var service = new OrbRecoveryService(store,
            new GameEventLogManager(id => store.Get(id)?.EventLog), NullLogger<OrbRecoveryService>.Instance);
        var bot = new BotPlayerState { PlayerId = 11, Corruption = 12 };
        var first = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var second = first with { WeaponItemId = 107000041, WeaponItemUid = 2 };
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        using (store.Enter(match))
        {
            service.Process(match.MatchingId, [first, second], [], [bot], now);
            Assert.Equal(12, bot.Corruption);
            service.Process(match.MatchingId, [first, second], [], [bot], now.AddSeconds(5));
            Assert.Equal(0, bot.Corruption);
            Assert.Equal(2, match.Presentation.OrbRecoveryReadyAtUtc.Count);
            service.Process(match.MatchingId, [], [], [bot], now.AddSeconds(6));
            Assert.Empty(match.Presentation.OrbRecoveryReadyAtUtc);
            match.TryMarkTerminal();
        }
    }

    [Fact]
    public void Recovery_DoesNotShareClocksBetweenMatches()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var firstMatch = store.GetOrCreate(947302);
        var secondMatch = store.GetOrCreate(947303);
        var service = new OrbRecoveryService(store,
            new GameEventLogManager(id => store.Get(id)?.EventLog), NullLogger<OrbRecoveryService>.Instance);
        var actor = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (store.Enter(firstMatch))
            service.Process(firstMatch.MatchingId, [actor], [], [], now);
        using (store.Enter(secondMatch))
        {
            var bot = new BotPlayerState { PlayerId = 11, Corruption = 10 };
            service.Process(secondMatch.MatchingId, [actor], [], [bot], now.AddSeconds(5));
            Assert.Equal(10, bot.Corruption);
            secondMatch.TryMarkTerminal();
        }
        using (store.Enter(firstMatch)) firstMatch.TryMarkTerminal();
    }
}
