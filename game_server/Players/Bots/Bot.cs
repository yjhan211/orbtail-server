using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>공통 Player를 참조하며 봇의 판단·경로·행동 대기 상태를 보관한다.</summary>
public class Bot
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


    // === #127 walking pathfinding ===
    public List<MapPathfinder.Step> Path { get; set; } = new();

    public int PathIndex { get; set; }

    public DateTime LastWalkStepTime { get; set; } = DateTime.UtcNow;

    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;

    /// <summary>잠긴 문 차단 로그의 중복 억제 — 같은 방에 연속으로 막히면 한 번만 남긴다.</summary>
    public AreaType LastLockedDoorBlockArea { get; set; } = AreaType.None;

    // 투사체 회피 커밋 (2026-08-18): 한 번 비켜서기 시작한 방향과 유지 시각. 유지 중에는 띠 밖에 나가도
    // 원래 경로로 되돌아가지 않고 제자리에 선다 — 띠 가장자리에서 들락거리는 떨림을 없앤다.
    public float SwarmDodgeDirectionX { get; set; }
    public float SwarmDodgeDirectionY { get; set; }
    public DateTime SwarmDodgeHoldUntilUtc { get; set; } = DateTime.MinValue;

    /// <summary>Safe room retained while the bot is travelling out of a warned area.</summary>
    public AreaType EvacuationDestination { get; set; } = AreaType.None;

    /// <summary>Room goal retained while the bot is travelling for loot, an interaction, or a target.</summary>
    public AreaType MovementDestination { get; set; } = AreaType.None;

    public SwarmBotMode SwarmMode { get; set; } = SwarmBotMode.None;
    public DateTime SwarmModeUntilUtc { get; set; } = DateTime.MinValue;

    /// <summary>마지막 피격 시각 (#222) — 피격 중에는 이동 계획 홀드를 무시하고 도주·추격을 판단하는 입력.</summary>
    public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;
    /// <summary>구역 기억 — 방금 떠난 구역으로 되돌아가는 왕복을 억제하는 근거.</summary>
    public (AreaType Area, AreaType PreviousArea, DateTime LeftAtUtc)? AreaMemory { get; set; }
    /// <summary>이번 틱 지시가 도주·대피였는가 — 왕복 억제의 유일한 예외.</summary>
    public bool FleeDirective { get; set; }
    /// <summary>도주 목적지 약속 — 유효 시간 안에는 같은 목적지를 유지해 재계획 폭주에도 방향이 뒤집히지 않는다.</summary>
    public (Vector3f Destination, DateTime CommittedAtUtc)? FleeCommitment { get; set; }
    /// <summary>마지막 절단 시각 — 절단 쿨다운과 절단 직후 전리품 회수 창의 기준.</summary>
    public DateTime? LastTrailCutAtUtc { get; set; }
    /// <summary>치명상 — 남은 체력 40% 이하에서 진입, 55% 이상에서 해제.</summary>
    public bool Wounded { get; set; }

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


}
