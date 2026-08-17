using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server;

/// <summary>
///     교차사격 (#232 2단계). 오브는 몬스터만 쏘지만, 그 공격이 만드는 모양(태양 = 직선)에
///     다른 플레이어가 서 있으면 고정 충격을 받는다. 발사 순간 원점(오브)·기준점(몬스터)을
///     잠그고, 예고 시간 뒤 짧은 판정 창 동안 선분-점 거리로 판정한다. 예고 뒤 몬스터가
///     죽어도 모양은 잠근 위치에서 끝까지 처리된다.
///     P0-A는 태양 직선만 켠다 — 바람 부채꼴·파도 원형은 직선이 읽힌 뒤 붙인다.
/// </summary>
public partial class GameServer
{
    private static readonly bool SwarmCrossfireEnabled = true;

    private readonly record struct SwarmCrossfireShape(
        long MatchingId,
        long EventId,
        long OwnerId,
        int WeaponItemId,
        int Shape,
        AreaType Area,
        Vector3f Origin,
        Vector3f End,
        float HalfWidth,
        DateTime ArmedAtUtc,
        DateTime ExpiresAtUtc,
        int AnchorMonsterId,
        HashSet<long> HitVictims);

    private readonly List<SwarmCrossfireShape> _swarmCrossfireShapes = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCrossfireVictimImmuneUntilUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCrossfireOwnerLastHitAtUtc = new();
    private long _swarmCrossfireEventSeq;

    /// <summary>
    ///     발사 순간 모양을 잠근다. 태양(빨강)만 직선을 만들고 나머지는 아직 모양이 없다.
    ///     소유자당 동시 예고 상한을 넘으면 이 발은 PvE 피해만 남긴다.
    /// </summary>
    private void TryScheduleSwarmCrossfire(
        long matchingId,
        ProximityCombatAttack attack,
        Vector3f? origin,
        Vector3f? anchor,
        int anchorMonsterId,
        DateTime nowUtc,
        List<GameClientSession> allSessions)
    {
        if (!SwarmCrossfireEnabled || origin == null || anchor == null)
            return;
        if (!SurvivorOrbData.TryGetColorAndTier(attack.WeaponItemId, out var color, out int tier) ||
            color != SurvivorOrbColor.Red)
            return;

        int activeForOwner = 0;
        foreach (var shape in _swarmCrossfireShapes)
        {
            if (shape.MatchingId == matchingId && shape.OwnerId == attack.AttackerPlayerId)
                activeForOwner++;
        }

        if (activeForOwner >= Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
            return;

        // 바닥면 기하 (2026-08-17 유저 판정: 범위가 타일을 따라가야 한다). 이 맵은 아이소 타일이라
        // 월드 Y가 화면 세로로 절반 눌려 있다 — 접촉·물폭탄과 같은 정규화(dy×2)로 바닥면에서
        // 방향·연장·폭을 재고, 월드로 되돌려 보낸다. 클라도 같은 면(Y 0.5 스케일)에 그린다.
        float gx = anchor.X - origin.X;
        float gy = (anchor.Y - origin.Y) * SwarmGroundYScale;
        float groundLength = MathF.Sqrt(gx * gx + gy * gy);
        if (groundLength < 0.05f)
            return;

        int tierIndex = Math.Clamp(tier, 1, 3) - 1;
        float extend = Config.SWARM_CROSSFIRE_SUN_EXTEND_BY_TIER[tierIndex];
        float width = Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER[tierIndex];
        var end = new Vector3f(
            anchor.X + gx / groundLength * extend,
            anchor.Y + gy / groundLength * extend / SwarmGroundYScale,
            0f);

        long eventId = ++_swarmCrossfireEventSeq;
        var armedAt = nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS);
        var expiresAt = armedAt.AddSeconds(Config.SWARM_CROSSFIRE_SUN_ACTIVE_SECONDS);
        _swarmCrossfireShapes.Add(new SwarmCrossfireShape(
            matchingId, eventId, attack.AttackerPlayerId, attack.WeaponItemId,
            Config.SWARM_CROSSFIRE_SHAPE_LINE, attack.Area,
            new Vector3f(origin.X, origin.Y, 0f), end, width * 0.5f,
            armedAt, expiresAt, anchorMonsterId, new HashSet<long>()));

        BroadcastSwarmCrossfireTelegraph(
            matchingId, eventId, attack, origin, end, width, anchorMonsterId, allSessions);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_TELEGRAPH event={eventId} owner={attack.AttackerPlayerId} " +
            $"weapon={attack.WeaponItemId} tier={tier} shape=line anchor={anchorMonsterId} " +
            $"area={attack.Area} origin=({origin.X:F2},{origin.Y:F2}) end=({end.X:F2},{end.Y:F2}) " +
            $"width={width:F2} telegraph={Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS:F2} " +
            $"active={Config.SWARM_CROSSFIRE_SUN_ACTIVE_SECONDS:F2}");
    }

    private static void BroadcastSwarmCrossfireTelegraph(
        long matchingId,
        long eventId,
        ProximityCombatAttack attack,
        Vector3f origin,
        Vector3f end,
        float width,
        int anchorMonsterId,
        List<GameClientSession> allSessions)
    {
        _ = matchingId;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_CROSSFIRE_TELEGRAPH);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_CROSSFIRE_TELEGRAPH
        {
            EventId = eventId,
            OwnerPlayerId = attack.AttackerPlayerId,
            WeaponItemId = attack.WeaponItemId,
            Shape = Config.SWARM_CROSSFIRE_SHAPE_LINE,
            OriginX = origin.X,
            OriginY = origin.Y,
            EndX = end.X,
            EndY = end.Y,
            Width = width,
            TelegraphSeconds = Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS,
            ActiveSeconds = Config.SWARM_CROSSFIRE_SUN_ACTIVE_SECONDS,
            AnchorMonsterId = anchorMonsterId,
            OwnerOrbOrdinal = attack.AttackerTrailOrdinal
        }));
        // 같은 구역 전원 — 소유자도 받는다. 자기 모양이 어디 생겼는지 봐야 다음 자리를 고른다.
        foreach (var session in allSessions)
        {
            if (session.PlayerId.HasValue && !session.IsEliminated && session.CurrentArea == attack.Area)
                session.Send(packet);
        }
    }

    /// <summary>
    ///     예고가 끝난 모양을 판정한다. 같은 구역의 소유자 아닌 살아 있는 플레이어가 캡슐 안에
    ///     있으면 충격 1회. 한 사건은 같은 피해자에게 한 번, 피해자는 전역 0.9초 면역,
    ///     소유자는 초당 1회만 유효 충격을 만든다.
    /// </summary>
    private void ProcessSwarmCrossfires(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        if (!SwarmCrossfireEnabled)
            return;

        for (int index = _swarmCrossfireShapes.Count - 1; index >= 0; index--)
        {
            var shape = _swarmCrossfireShapes[index];
            if (shape.MatchingId != matchingId)
                continue;
            if (nowUtc > shape.ExpiresAtUtc)
            {
                _swarmCrossfireShapes.RemoveAt(index);
                continue;
            }

            if (nowUtc < shape.ArmedAtUtc)
                continue;

            foreach (var participant in participants)
            {
                if (participant.PlayerId == shape.OwnerId || participant.Area != shape.Area)
                    continue;
                if (shape.HitVictims.Contains(participant.PlayerId))
                    continue;
                if (_swarmCrossfireVictimImmuneUntilUtc.TryGetValue(
                        (matchingId, participant.PlayerId), out var immuneUntil) &&
                    nowUtc < immuneUntil)
                    continue;
                if (_swarmCrossfireOwnerLastHitAtUtc.TryGetValue(
                        (matchingId, shape.OwnerId), out var ownerLastHit) &&
                    (nowUtc - ownerLastHit).TotalSeconds < Config.SWARM_CROSSFIRE_OWNER_HIT_INTERVAL_SECONDS)
                    continue;
                if (!IsPointInsideSwarmCrossfireLine(shape, participant.Position))
                    continue;

                shape.HitVictims.Add(participant.PlayerId);
                _swarmCrossfireVictimImmuneUntilUtc[(matchingId, participant.PlayerId)] =
                    nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_VICTIM_IMMUNE_SECONDS);
                _swarmCrossfireOwnerLastHitAtUtc[(matchingId, shape.OwnerId)] = nowUtc;
                ApplySwarmCrossfireShock(matchingId, shape, participant.PlayerId, aliveSessions, aliveBots, allSessions);
            }
        }
    }

    // 아이소 바닥면 정규화 계수 — 접촉 판정·물폭탄 반경과 같은 dy×2.
    private const float SwarmGroundYScale = 2f;

    /// <summary>
    ///     캡슐 판정 (바닥면): 점과 선분의 최단 거리가 반폭 이하 — 월드 Y를 2배로 편 정규화
    ///     공간에서 잰다. 클라 캡슐 스프라이트(Y 0.5 스케일 부모 아래 회전)와 같은 기하다.
    /// </summary>
    private static bool IsPointInsideSwarmCrossfireLine(SwarmCrossfireShape shape, Vector3f point)
    {
        float ax = shape.Origin.X, ay = shape.Origin.Y * SwarmGroundYScale;
        float bx = shape.End.X, by = shape.End.Y * SwarmGroundYScale;
        float px = point.X, py = point.Y * SwarmGroundYScale;
        float abx = bx - ax, aby = by - ay;
        float lengthSquared = abx * abx + aby * aby;
        float t = lengthSquared <= 0f
            ? 0f
            : Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lengthSquared, 0f, 1f);
        float cx = ax + abx * t, cy = ay + aby * t;
        float dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= shape.HalfWidth * shape.HalfWidth;
    }

    private void ApplySwarmCrossfireShock(
        long matchingId,
        SwarmCrossfireShape shape,
        long victimId,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        int shock = Config.SWARM_CROSSFIRE_SHOCK_CORRUPTION;
        int corruptionBefore;
        int corruptionAfter;

        var victimSession = aliveSessions.FirstOrDefault(session => session.PlayerId == victimId);
        if (victimSession != null)
        {
            corruptionBefore = victimSession.CurrentCorruption;
            // 사격 피격 경로 재사용 — 오염 증가·피격 숫자·탈락 흐름이 그대로 따라온다.
            victimSession.ApplyProximityAutoCombatHit(shape.OwnerId, shape.Area, shape.WeaponItemId, shock);
            corruptionAfter = victimSession.CurrentCorruption;
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == victimId);
            if (bot == null)
                return;
            corruptionBefore = bot.Corruption;
            bot.LastProximityAttackerPlayerId = shape.OwnerId;
            _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            _gameEventLogManager.LogSurvivorHit(
                matchingId, shape.OwnerId, bot.PlayerId, shape.WeaponItemId, shock,
                bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION &&
                bot.Corruption + shock >= Config.SURVIVOR_MAX_CORRUPTION,
                BotPlayerManager.IsBotPlayerId(shape.OwnerId), DateTimeOffset.UtcNow);
            bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + shock);
            corruptionAfter = bot.Corruption;
        }

        var ownerSession = allSessions.FirstOrDefault(session => session.PlayerId == shape.OwnerId);
        ownerSession?.SendProximityAutoCombatAttackFeedback(victimId, shape.Area, shape.WeaponItemId, shock);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_HIT event={shape.EventId} owner={shape.OwnerId} victim={victimId} " +
            $"weapon={shape.WeaponItemId} shape=line anchor={shape.AnchorMonsterId} " +
            $"corruptionBefore={corruptionBefore} corruptionAfter={corruptionAfter}");
    }

    private void ClearSwarmCrossfireState(long matchingId)
    {
        _swarmCrossfireShapes.RemoveAll(shape => shape.MatchingId == matchingId);
        foreach (var key in _swarmCrossfireVictimImmuneUntilUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireVictimImmuneUntilUtc.Remove(key);
        foreach (var key in _swarmCrossfireOwnerLastHitAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireOwnerLastHitAtUtc.Remove(key);
    }
}
