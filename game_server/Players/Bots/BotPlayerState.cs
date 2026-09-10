using game_server.players;
using game_server.combat;
using network.common;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>공통 Player를 참조하며 봇의 판단·경로·행동 대기 상태를 보관한다.</summary>
public class BotPlayerState
{
    /// <summary>매치 참가자와 공유하는 체력·위치·탈락 상태. 봇의 판단·경로 상태는 별도로 보관한다.</summary>
    // 사람은 입장 준비 전 위치가 없을 수 있지만, 봇은 생성 시 셀과 위치를 항상 초기화한다.
    public Player Player { get; } = new()
    {
        Profile = new PlayerInfo(),
        Cell = new(0, 0),
        Position = new(0f, 0f, 0f)
    };
    public long PlayerId { get => Player.PlayerId; set => Player.Profile.PlayerId = value; }

    public long LastProximityAttackerPlayerId { get; set; }
    public List<int> ActiveBuffIds { get; set; } = new();

    public string Name { get; set; } = "";

    // === #127 walking pathfinding ===
    public List<BotPathfinder.Step> Path { get; set; } = new();

    public int PathIndex { get; set; }

    public DateTime LastWalkStepTime { get; set; } = DateTime.UtcNow;

    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;


    /// <summary>
    ///     현재 지역에 들어온 시각. 방 사냥이 진전 없이 길어졌는지 판정하는 기준이다.
    ///     정상적인 팩 정리는 20초 안에 끝나므로, 이 시각이 오래되면 그 방을 목적지 후보에서 뺀다.
    /// </summary>

    /// <summary>잠긴 문 차단 로그의 중복 억제 — 같은 방에 연속으로 막히면 한 번만 남긴다.</summary>
    public AreaType LastLockedDoorBlockArea { get; set; } = AreaType.None;

    /// <summary>
    ///     정체가 감지되어 현재 방을 떠나야 한다는 요청. 이동 루프 상단에서 세우고
    ///     잔상 사냥 계획이 소비한다. 목적지 커밋이 사냥 계획을 가로막기 때문에 두 단계로 나눈다.
    /// </summary>

    public bool IsChannelHeld { get; set; }

    public DateTime ChannelHoldUntil { get; set; } = DateTime.MinValue;

    public AreaType PendingForcedInteractArea { get; set; } = AreaType.None;

    public int PendingForcedInteractId { get; set; }




    public DateTime RestUntil { get; set; } = DateTime.MinValue;


    public DateTime GameStartTime { get; set; } = DateTime.UtcNow;



    // 카이팅 접선 방향 (2026-08-18 유저 지시 "제자리 좌우 와리가리 금지"): 초마다 좌우를 바꾸던 것을
    // 봇마다 한쪽으로 고정한다 — 그쪽이 막혔을 때만 뒤집는다. 0이면 미정(봇 id 홀짝으로 정한다).

    // 투사체 회피 커밋 (2026-08-18): 한 번 비켜서기 시작한 방향과 유지 시각. 유지 중에는 띠 밖에 나가도
    // 원래 경로로 되돌아가지 않고 제자리에 선다 — 띠 가장자리에서 들락거리는 떨림을 없앤다.
    public float SwarmDodgeDirectionX { get; set; }
    public float SwarmDodgeDirectionY { get; set; }
    public DateTime SwarmDodgeHoldUntilUtc { get; set; } = DateTime.MinValue;

    /// <summary>Safe room retained while the bot is travelling out of a warned area.</summary>
    public AreaType EvacuationDestination { get; set; } = AreaType.None;

    /// <summary>Room goal retained while the bot is travelling for loot, an interaction, or a target.</summary>
    public AreaType MovementDestination { get; set; } = AreaType.None;

    /// <summary>Safe room selected during #214 corridor selection.</summary>

    public SwarmBotMode SwarmMode { get; set; } = SwarmBotMode.None;
    public DateTime SwarmModeUntilUtc { get; set; } = DateTime.MinValue;











    public void HoldForChannel(TimeSpan fallbackDuration)
    {
        IsChannelHeld = true;
        ChannelHoldUntil = DateTime.UtcNow.Add(fallbackDuration);
    }

    /// <summary>위협 감지 시 채집·상호작용 홀드를 즉시 끊는다 — 홀드 채로 맞다 죽는 사고 방지.</summary>
    public void CancelChannelHold()
    {
        IsChannelHeld = false;
        ChannelHoldUntil = DateTime.MinValue;
    }

    /// <summary>마지막 피격 시각 (#222) — 피격 중에는 이동 계획 홀드를 무시하는 판단 입력.</summary>
    public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;

    // 유휴 감시 (#222): 6초 이상 제자리면 원인 진단 로그를 남긴다 — "가만히 서 있는 봇" 추적.
    public Vector3f? IdleWatchLastPosition { get; set; }
    public DateTime IdleWatchLastMovedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime IdleWatchLastLoggedAtUtc { get; set; } = DateTime.MinValue;

    // 유휴 배회 (#222): 도착 대기(캠프 리스폰·사격 대기)로 서 있지 않게 주변을 서성인다.
    public DateTime NextIdleWanderAtUtc { get; set; } = DateTime.MinValue;

    // 부츠: 사람과 같은 규칙으로 봇도 쓴다.
    public DateTime BootsSpeedUntilUtc { get; set; } = DateTime.MinValue;

    // 빈손 이속 (#223): 지시 판단 틱이 갱신 — 사람과 같은 배율로 도주가 성립하게.
    public bool IsSwarmBareHanded { get; set; }

    // 빈손 가속 만료 (#229 12단계): 마지막 오브를 잃은 직후 2초만 빨라진다.
    // 빈손인 내내 빠르면 "패배 직전"이 아니라 도주 특화 상태가 된다.
    public DateTime SwarmBareSpeedUntilUtc { get; set; } = DateTime.MinValue;

    public int PendingRngInteractId { get; set; }


    /// <summary>이번 매치에서 이 봇이 탐색을 끝낸 방. 방을 이동해도 유지한다.</summary>

    /// <summary>이번 매치에서 이 봇이 실제 RNG 탐색을 완료한 상호작용 지점.</summary>





    /// <summary>Server-authoritative movement multiplier from currently living Wind orbs.</summary>
    public float WindMoveSpeedMultiplier { get; set; } = 1f;

    /// <summary>Temporary movement slow applied by a wave counter.</summary>
    public DateTime WaveSlowUntilUtc { get; set; }


    // #229: 문 잠금해제 게이지. 사람과 같은 규칙 — 맞으면 풀린다(LastDamagedAtUtc 참조).
    public int SwarmDoorUnlockDoorId { get; set; }
    public DateTime SwarmDoorUnlockStartedAtUtc { get; set; } = DateTime.MinValue;
}
