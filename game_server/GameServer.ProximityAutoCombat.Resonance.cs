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
        SurvivorOrbColor ActiveColor,
        int SunStage,
        bool WindActive,
        bool WindJustActivated,
        bool WaveArmed,
        int HighestWaveItemId);
}
