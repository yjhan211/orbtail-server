using game_server.network;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

public partial class GameServer
{
    private readonly Dictionary<(long MatchingId, long PlayerId), SurvivorOrbResonanceRuntimeState>
        _survivorOrbResonanceStates = new();
    private readonly Dictionary<(long MatchingId, long AttackerPlayerId, long TargetPlayerId), SunLightMarkState>
        _sunLightMarks = new();

    private IReadOnlyDictionary<long, SurvivorOrbResonanceSnapshot> UpdateSurvivorOrbResonanceStates(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions,
        IReadOnlyCollection<BotPlayerState> bots,
        DateTime nowUtc)
    {
        var snapshots = new Dictionary<long, SurvivorOrbResonanceSnapshot>();
        var activePlayerIds = new HashSet<long>();

        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue || session.LastValidatedPosition == null)
                continue;

            long playerId = session.PlayerId.Value;
            activePlayerIds.Add(playerId);
            var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
            snapshots[playerId] = UpdateSurvivorOrbResonanceState(
                matchingId,
                playerId,
                session.LastValidatedPosition,
                inventory.GetAllItems(),
                nowUtc);
        }

        foreach (var bot in bots)
        {
            activePlayerIds.Add(bot.PlayerId);
            var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
            var snapshot = UpdateSurvivorOrbResonanceState(
                matchingId,
                bot.PlayerId,
                bot.Position,
                inventory.GetAllItems(),
                nowUtc);
            bot.WindResonanceActive = snapshot.WindActive;
            snapshots[bot.PlayerId] = snapshot;
        }

        foreach (var key in _survivorOrbResonanceStates.Keys
                     .Where(key => key.MatchingId == matchingId && !activePlayerIds.Contains(key.PlayerId))
                     .ToArray())
        {
            _survivorOrbResonanceStates.Remove(key);
        }

        return snapshots;
    }

    private SurvivorOrbResonanceSnapshot UpdateSurvivorOrbResonanceState(
        long matchingId,
        long playerId,
        Vector3f position,
        IEnumerable<InGameItemInfo> items,
        DateTime nowUtc)
    {
        var key = (matchingId, playerId);
        if (!_survivorOrbResonanceStates.TryGetValue(key, out var state))
        {
            state = new SurvivorOrbResonanceRuntimeState();
            _survivorOrbResonanceStates[key] = state;
        }

        int sunCount = 0;
        int windCount = 0;
        int waveCount = 0;
        int highestWaveItemId = 0;
        int highestWaveTier = 0;
        foreach (var item in items.Where(item => item.Count > 0))
        {
            if (!SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var color, out int tier))
                continue;

            switch (color)
            {
                case SurvivorOrbColor.Red:
                    sunCount += item.Count;
                    break;
                case SurvivorOrbColor.Green:
                    windCount += item.Count;
                    break;
                case SurvivorOrbColor.Blue:
                    waveCount += item.Count;
                    if (tier > highestWaveTier)
                    {
                        highestWaveTier = tier;
                        highestWaveItemId = item.ItemId;
                    }
                    break;
            }
        }

        bool moved = state.LastPosition != null && DistanceSquared(state.LastPosition, position) >= 0.0025f;
        if (moved)
        {
            if (state.LastMovementAtUtc == DateTime.MinValue ||
                nowUtc - state.LastMovementAtUtc > TimeSpan.FromSeconds(0.25))
            {
                state.MovingSinceUtc = nowUtc;
            }
            state.LastMovementAtUtc = nowUtc;
        }
        else if (state.LastMovementAtUtc != DateTime.MinValue &&
                 nowUtc - state.LastMovementAtUtc > TimeSpan.FromSeconds(0.25))
        {
            state.MovingSinceUtc = DateTime.MinValue;
        }

        state.LastPosition = position;
        bool windActive = windCount >= 2 &&
                          state.MovingSinceUtc != DateTime.MinValue &&
                          nowUtc - state.MovingSinceUtc >= TimeSpan.FromSeconds(SurvivorOrbData.WindChargeSeconds) &&
                          state.LastDamageAtUtc <= state.MovingSinceUtc;
        bool windJustActivated = windActive && !state.WindWasActive;
        state.WindWasActive = windActive;

        state.SunCount = sunCount;
        state.WindCount = windCount;
        state.WaveCount = waveCount;
        state.HighestWaveItemId = highestWaveItemId;

        return new SurvivorOrbResonanceSnapshot(
            SurvivorOrbData.GetSunResonanceStage(sunCount),
            windActive,
            windJustActivated,
            waveCount >= 2,
            highestWaveItemId);
    }

    private void NotifySurvivorOrbResonanceDamaged(long matchingId, long playerId, DateTime nowUtc)
    {
        if (!_survivorOrbResonanceStates.TryGetValue((matchingId, playerId), out var state))
            return;

        state.LastDamageAtUtc = nowUtc;
        state.MovingSinceUtc = DateTime.MinValue;
    }

    private bool TryConsumeSunConcentration(
        long matchingId,
        ProximityCombatAttack attack,
        DateTime nowUtc)
    {
        if (attack.IsResonanceProc || attack.SunResonanceStage < 1)
            return false;

        var key = (matchingId, attack.AttackerPlayerId, attack.TargetPlayerId);
        if (!_sunLightMarks.TryGetValue(key, out var marks) ||
            nowUtc - marks.LastAppliedAtUtc > TimeSpan.FromSeconds(SurvivorOrbData.SunMarkLifetimeSeconds))
        {
            marks = new SunLightMarkState();
        }

        marks.Count = Math.Min(SurvivorOrbData.SunMarkTriggerCount, marks.Count + 1);
        marks.LastAppliedAtUtc = nowUtc;
        if (attack.SunResonanceStage < 3 || marks.Count < SurvivorOrbData.SunMarkTriggerCount)
        {
            _sunLightMarks[key] = marks;
            return false;
        }

        _sunLightMarks.Remove(key);
        return true;
    }

    private bool TryTriggerWaveCounter(
        long matchingId,
        long targetPlayerId,
        long attackerPlayerId,
        DateTime nowUtc,
        IReadOnlyDictionary<long, SurvivorOrbResonanceSnapshot> snapshots,
        out int waveItemId,
        out int damage)
    {
        waveItemId = 0;
        damage = 0;
        if (!snapshots.TryGetValue(targetPlayerId, out var snapshot) ||
            !snapshot.WaveArmed || snapshot.HighestWaveItemId <= 0 ||
            !_survivorOrbResonanceStates.TryGetValue((matchingId, targetPlayerId), out var state) ||
            nowUtc < state.WaveCounterReadyAtUtc)
        {
            return false;
        }

        if (state.WaveHitWindowStartedAtUtc == DateTime.MinValue ||
            nowUtc - state.WaveHitWindowStartedAtUtc > TimeSpan.FromSeconds(SurvivorOrbData.WaveHitWindowSeconds))
        {
            state.WaveHitWindowStartedAtUtc = nowUtc;
            state.WaveHitsInWindow = 0;
        }

        state.WaveHitsInWindow++;
        state.LastWaveAttackerPlayerId = attackerPlayerId;
        if (state.WaveHitsInWindow < 2)
            return false;

        var combatData = BattleItemCombatData.Get(snapshot.HighestWaveItemId);
        if (combatData == null)
            return false;

        state.WaveHitsInWindow = 0;
        state.WaveHitWindowStartedAtUtc = DateTime.MinValue;
        state.WaveCounterReadyAtUtc = nowUtc.AddSeconds(SurvivorOrbData.WaveCounterCooldownSeconds);
        waveItemId = snapshot.HighestWaveItemId;
        damage = Math.Max(1, (int)Math.Ceiling(combatData.Damage * SurvivorOrbData.WaveCounterDamageMultiplier));
        return true;
    }

    private void RemoveSurvivorOrbResonanceStates(long matchingId)
    {
        foreach (var key in _survivorOrbResonanceStates.Keys.Where(key => key.MatchingId == matchingId).ToArray())
            _survivorOrbResonanceStates.Remove(key);
        foreach (var key in _sunLightMarks.Keys.Where(key => key.MatchingId == matchingId).ToArray())
            _sunLightMarks.Remove(key);
    }

    private static float DistanceSquared(Vector3f left, Vector3f right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

    private sealed class SurvivorOrbResonanceRuntimeState
    {
        public Vector3f? LastPosition { get; set; }
        public DateTime LastMovementAtUtc { get; set; }
        public DateTime MovingSinceUtc { get; set; }
        public DateTime LastDamageAtUtc { get; set; }
        public int SunCount { get; set; }
        public int WindCount { get; set; }
        public int WaveCount { get; set; }
        public int HighestWaveItemId { get; set; }
        public bool WindWasActive { get; set; }
        public DateTime WaveHitWindowStartedAtUtc { get; set; }
        public int WaveHitsInWindow { get; set; }
        public long LastWaveAttackerPlayerId { get; set; }
        public DateTime WaveCounterReadyAtUtc { get; set; }
    }

    private sealed class SunLightMarkState
    {
        public int Count { get; set; }
        public DateTime LastAppliedAtUtc { get; set; }
    }

    private readonly record struct SurvivorOrbResonanceSnapshot(
        int SunStage,
        bool WindActive,
        bool WindJustActivated,
        bool WaveArmed,
        int HighestWaveItemId);
}