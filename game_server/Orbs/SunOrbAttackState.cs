using game_server.combat;
using game_server.players.bots;
using network.common;
using network.common.data.models;

namespace game_server.orbs;

/// <summary>One locked Crossfire line. Mutable hit/front fields advance under the match gate.</summary>
internal sealed class SwarmCrossfireShape
{
    public long EventId { get; init; }
    public long OwnerId { get; init; }
    public int WeaponItemId { get; init; }
    public int Damage { get; init; }
    public AreaType Area { get; init; }
    public Vector3f Origin { get; init; } = new(0f, 0f, 0f);
    public Vector3f End { get; init; } = new(0f, 0f, 0f);
    public float GroundLength { get; init; }
    public float HalfWidth { get; init; }
    public float BlastRadius { get; init; }
    public float SweepSpeed { get; init; }
    public DateTime ArmedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public bool DetonateAtWall { get; init; }
    public int AnchorMonsterId { get; init; }
    public long AnchorCombatTargetId { get; init; }
    public float LastFront { get; set; }
    public HashSet<long> HitVictims { get; } = new();
    public HashSet<long> HitMonsters { get; } = new();
}

internal sealed record SwarmSunBurnState(
    long OwnerId,
    int WeaponItemId,
    AreaType Area,
    DateTime UntilUtc,
    DateTime NextTickAtUtc);

internal readonly record struct SwarmCrossfireConvergenceObservation(
    int HitCount,
    double WindowMilliseconds);

/// <summary>
///     매치 하나의 태양오브 발사체, 화상, 연속 명중 기록과 봇 회피용 정보를 보관한다.
///     상태 변경은 매치 잠금 안에서 수행하며, 봇은 별도로 공개한 회피 정보만 읽는다.
/// </summary>
public sealed class SunOrbAttackState
{
    private readonly long _matchingId;
    // 매치가 제거·재생성돼도 공격 이벤트 ID를 재사용하지 않는다.
    private static long _lastEventId;
    private readonly List<SwarmCrossfireShape> _shapes = new();
    private readonly Dictionary<long, SwarmSunBurnState> _sunBurns = new();
    private readonly Dictionary<long, (DateTime WindowStartUtc, int Count)> _convergenceWindows = new();
    private SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat[] _dodgeSnapshot = [];

    internal SunOrbAttackState(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        _matchingId = matchingId;
    }

    public bool IsEmpty =>
        _shapes.Count == 0 &&
        _sunBurns.Count == 0 &&
        _convergenceWindows.Count == 0;

    internal int ShapeCount => _shapes.Count;
    internal int SunBurnCount => _sunBurns.Count;
    internal int ConvergenceWindowCount => _convergenceWindows.Count;

    internal IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> DodgeSnapshot =>
        Volatile.Read(ref _dodgeSnapshot);

    internal long AllocateEventId() => Interlocked.Increment(ref _lastEventId);

    internal int CountTelegraphing(long ownerId, DateTime nowUtc)
    {
        int count = 0;
        foreach (SwarmCrossfireShape shape in _shapes)
        {
            if (shape.OwnerId == ownerId && nowUtc < shape.ArmedAtUtc)
                count++;
        }

        return count;
    }

    internal HashSet<(long OwnerId, long CombatTargetId)> CollectAnchoredTargets()
    {
        var anchored = new HashSet<(long, long)>();
        foreach (SwarmCrossfireShape shape in _shapes)
            anchored.Add((shape.OwnerId, shape.AnchorCombatTargetId));
        return anchored;
    }

    internal HashSet<long> CollectCappedOwners(DateTime nowUtc, int maximumTelegraphsPerOwner)
    {
        var telegraphingByOwner = new Dictionary<long, int>();
        foreach (SwarmCrossfireShape shape in _shapes)
        {
            if (nowUtc >= shape.ArmedAtUtc)
                continue;
            telegraphingByOwner[shape.OwnerId] = telegraphingByOwner.GetValueOrDefault(shape.OwnerId) + 1;
        }

        var capped = new HashSet<long>();
        foreach ((long ownerId, int count) in telegraphingByOwner)
        {
            if (count >= maximumTelegraphsPerOwner)
                capped.Add(ownerId);
        }

        return capped;
    }

    internal SwarmCrossfireShape GetShapeAt(int index) => _shapes[index];

    internal void AddShape(SwarmCrossfireShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        _shapes.Add(shape);
        PublishDodgeSnapshot();
    }

    internal void RemoveShapeAt(int index) => _shapes.RemoveAt(index);

    /// <summary>
    ///     Publishes after a completed processing pass. Callers deliberately do this once after all
    ///     removals so one arena tick produces one replacement snapshot, matching legacy behavior.
    /// </summary>
    internal void PublishDodgeSnapshot()
    {
        var threats = new List<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat>(_shapes.Count);
        foreach (SwarmCrossfireShape shape in _shapes)
        {
            float axisX = shape.End.X - shape.Origin.X;
            float axisY = (shape.End.Y - shape.Origin.Y) * SwarmCombatGeometry.GroundYScale;
            float length = MathF.Sqrt(axisX * axisX + axisY * axisY);
            if (length < 0.01f)
                continue;

            threats.Add(new SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat(
                _matchingId,
                shape.Area,
                shape.OwnerId,
                shape.Origin.X,
                shape.Origin.Y,
                axisX / length,
                axisY / length,
                shape.GroundLength,
                shape.HalfWidth,
                shape.SweepSpeed,
                shape.ArmedAtUtc,
                shape.ExpiresAtUtc));
        }

        Volatile.Write(ref _dodgeSnapshot, threats.ToArray());
    }

    internal void SetSunBurn(
        long victimId,
        long ownerId,
        int weaponItemId,
        AreaType area,
        DateTime nowUtc,
        double durationSeconds,
        double tickIntervalSeconds) =>
        _sunBurns[victimId] = new SwarmSunBurnState(
            ownerId,
            weaponItemId,
            area,
            nowUtc.AddSeconds(durationSeconds),
            nowUtc.AddSeconds(tickIntervalSeconds));

    internal bool TryGetSunBurn(long victimId, out SwarmSunBurnState? burn) =>
        _sunBurns.TryGetValue(victimId, out burn);

    /// <summary>
    ///     Applies at most one due tick per burn per call. At the exact expiry boundary the final
    ///     due tick runs before removal. State advances only after the callback succeeds.
    /// </summary>
    internal void ProcessSunBurns(
        DateTime nowUtc,
        double tickIntervalSeconds,
        Action<long, SwarmSunBurnState> applyTick)
    {
        ArgumentNullException.ThrowIfNull(applyTick);

        List<long>? expired = null;
        foreach (KeyValuePair<long, SwarmSunBurnState> pair in _sunBurns)
        {
            SwarmSunBurnState burn = pair.Value;
            if (nowUtc >= burn.NextTickAtUtc)
            {
                applyTick(pair.Key, burn);
                _sunBurns[pair.Key] = burn with
                {
                    NextTickAtUtc = burn.NextTickAtUtc.AddSeconds(tickIntervalSeconds)
                };
            }

            if (nowUtc >= burn.UntilUtc)
                (expired ??= new List<long>()).Add(pair.Key);
        }

        if (expired == null)
            return;
        foreach (long victimId in expired)
            _sunBurns.Remove(victimId);
    }

    internal SwarmCrossfireConvergenceObservation TrackConvergence(long targetId, DateTime nowUtc)
    {
        if (_convergenceWindows.Count > 512)
        {
            foreach (long staleTargetId in _convergenceWindows
                         .Where(pair => (nowUtc - pair.Value.WindowStartUtc).TotalSeconds > 1d)
                         .Select(pair => pair.Key)
                         .ToList())
                _convergenceWindows.Remove(staleTargetId);
        }

        if (!_convergenceWindows.TryGetValue(targetId, out var window) ||
            (nowUtc - window.WindowStartUtc).TotalSeconds > 1d)
            window = (nowUtc, 0);

        window.Count++;
        _convergenceWindows[targetId] = window;
        return new SwarmCrossfireConvergenceObservation(
            window.Count,
            (nowUtc - window.WindowStartUtc).TotalMilliseconds);
    }
}
