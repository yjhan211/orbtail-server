// ReSharper disable All
using System;

namespace network.common
{
    /// <summary>
    /// 네트워크 및 서버 관련 설정 상수
    /// </summary>
    public class Config
    {
        private const int BroadcastChunkSize = 200;

        // Network Settings
        /// <summary>최대 동시 연결 수</summary>
        public static readonly int MAX_CONNECTION = 1000;

        /// <summary>소켓 당 미리 할당할 버퍼 개수</summary>
        public static readonly int PRE_ALLOC_COUNT = 2;

        /// <summary>패킷 버퍼 크기 (바이트)</summary>
        public static readonly int BUFFER_SIZE = 2048;

        /// <summary>패킷 헤더 크기 (바이트) - 길이 정보</summary>
        public static readonly int HEADER_SIZE = 4;

        /// <summary>리스닝 소켓 백로그 큐 크기</summary>
        public static readonly int BACK_LOG = 100;

        // Batch Processing Settings
        /// <summary>Redis 배치 작업 크기</summary>
        public static readonly int BATCH_SIZE = 10;

        /// <summary>브로드캐스트 단위 (패킷당 최대 오브젝트 수)</summary>
        public static readonly int BROADCAST_UNIT = BUFFER_SIZE / BroadcastChunkSize;

        // Game Logic Settings
        /// <summary>이동 큐 최대 크기</summary>
        public static readonly int MAX_MOVE_QUEUE_SIZE = 10;

        /// <summary>채팅 메시지 최대 길이</summary>
        public static readonly int MAX_CHAT_LENGTH = 30;

        // Lock Settings
        /// <summary>분산 락 TTL</summary>
        public static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(30);

        // Corruption 임계값
        /// <summary>불안 단계 시작 (정신 오염도 %)</summary>
        public const int CORRUPTION_UNEASE = 25;

        /// <summary>혼란 단계 시작 (정신 오염도 %)</summary>
        public const int CORRUPTION_CONFUSION = 50;

        /// <summary>광기 단계 시작 (정신 오염도 %)</summary>
        public const int CORRUPTION_MADNESS = 75;

        // Game Session Settings
        /// <summary>게임 세션 지속 시간 (분)</summary>
        public static readonly int GAME_DURATION_MINUTES = 15;

        /// <summary>게임 세션 지속 시간 (초)</summary>
        public static readonly int GAME_DURATION_SECONDS = GAME_DURATION_MINUTES * 60;

        /// <summary>Round/settlement loop toggle. Disabled for the current continuous-session prototype.</summary>
        public static readonly bool ROUND_SYSTEM_ENABLED = false;

        /// <summary>Round system: total round count (#168).</summary>
        public const int ROUND_TOTAL_COUNT = 4;

        /// <summary>Round system: action phase duration in seconds (#168).</summary>
        public const int ROUND_ACTION_SECONDS = 3 * 60;

        /// <summary>Round system: settlement phase duration in seconds (#168).</summary>
        public const int ROUND_SETTLEMENT_SECONDS = 45;
        public const int ROUND_SETTLEMENT_NOMINATION_SECONDS = 10;
        public const int ROUND_SETTLEMENT_RESULT_SECONDS = 3;
        public const int ROUND_SETTLEMENT_CONTRIBUTION_SECONDS = 3;
        public const int ROUND_SETTLEMENT_DETECTION_RESULT_SECONDS = 3;
        public const int ROUND_SETTLEMENT_ELIMINATION_SECONDS = 3;

        /// <summary>흔적 배치 스태미나 비용. GDD §3.1.2, 패키지 Y 2A: -10 → -5.</summary>
        public const int TRACE_PLACE_STAMINA_COST = 5;

        /// <summary>사보타주 4B: ▓▓ 위치 공개 지속 시간 (초). GDD §2.5.4.</summary>
        public const int SABOTAGE_TARGET_EXPOSE_SECONDS = 5;

        /// <summary>폐쇄 구역 체류 시 5초당 오염도 증가량. GDD §2.1.5, v0.1.9, #66.
        ///     스태미나 패널티(-20/5초)에서 오염도 패널티로 변경.
        ///     메타포: 밀폐된 위험 구역 체류 = 정신적 압박 상승.
        ///     사건·전투 등 다른 오염도 변화와 합산된다.
        ///     수치는 플레이테스트 후 최종 확정 예정 (미확정 #67). v0.1.10에서 2 → 4 → 5로 상향.</summary>
        public const int CLOSED_AREA_CORRUPTION_TICK = 5;

        /// <summary>근접 자동전투와 타겟 근접 체크리스트의 공통 거리 기준.</summary>
        public const float TARGET_PROXIMITY_DISTANCE = 3f;

        /// <summary>Survivor Royale inventory slot capacity shared by clients, bots, and the game server.</summary>
        public const int SURVIVOR_INVENTORY_SLOT_COUNT = 6;

        /// <summary>World pickup used to represent one summon stone.</summary>
        public const int SUMMON_STONE_GROUND_ITEM_ID = 107000050;

        /// <summary>Survivor Royale combat and closure elimination threshold.</summary>
        public const int SURVIVOR_MAX_CORRUPTION = 420;

        /// <summary>근접 자동전투 P0. 활성화 중에는 기존 수동 분필 공격 진입을 숨긴다.</summary>
        public static readonly bool PROXIMITY_AUTO_COMBAT_P0_ENABLED = true;

        /// <summary>
        /// Survivor Royale #202 uses monster rewards as summon currency instead of direct orb exploration loot.
        /// Legacy area pools stay loadable for data validation and isolated regression tests.
        /// </summary>
        public static readonly bool MONSTER_SUMMON_ECONOMY_ENABLED = true;

        /// <summary>
        /// Issue #216 vertical slice: four linked home spots, marching waves, and respawning players.
        /// This branch intentionally bypasses the orb economy and the #214 room phase machine.
        /// </summary>
        public static readonly bool SPOT_ARENA_P0_ENABLED = true;

        /// <summary>
        ///     Issue #217 스웜 회피 P0-a: 사람 1명 + 패턴 스폰 잔상 스웜. 잔상은 공급이 아니라
        ///     회피해야 하는 압력이다. 켜지면 스팟 아레나 대신 스웜 아레나가 매치를 소유한다.
        ///     SPOT_ARENA_P0_ENABLED는 레거시 시스템(폐쇄·오브 경제·페이즈)을 끄는 게이트로 유지한다.
        /// </summary>
        public static readonly bool SWARM_P0_ENABLED = true;

        /// <summary>
        ///     스웜 아레나 매치 정원. P0-a는 1(솔로), P0-b는 2, 3쌍 깔때기(성장곡선 v3)는 6.
        ///     사람은 항상 1명이고 나머지는 봇으로 채운다.
        /// </summary>
        public static readonly int SWARM_PLAYERS_PER_MATCH = 6;

        /// <summary>
        ///     스웜 탐색 스팟 개봉 비용 비례식(성장곡선 v3): 기본 3석 + 보유 오브당 2석.
        ///     시작 오브 1개 기준 첫 개봉 5석 — 종전 고정 비용과 같은 체감이다.
        ///     머지가 보유 수를 줄여 다음 개봉을 싸게 한다 (전투+경제 이중 인센티브). 사람·봇 공통.
        /// </summary>
        public const int SWARM_EXPLORE_COST_BASE = 3;

        /// <summary>보유 오브 1개당 개봉 비용 가산.</summary>
        public const int SWARM_EXPLORE_COST_PER_ORB = 2;

        public static int GetSwarmExploreCost(int ownedOrbCount) =>
            SWARM_EXPLORE_COST_BASE + SWARM_EXPLORE_COST_PER_ORB * (ownedOrbCount > 0 ? ownedOrbCount : 0);

        /// <summary>
        ///     스웜 탐색 스팟은 한 번 열면 판이 끝날 때까지 소진된다 — 방을 떠날 이유를 만든다.
        ///     쿨다운 저장소를 재사용하므로 "판보다 긴 쿨다운"으로 표현한다.
        /// </summary>
        public const int SWARM_EXPLORE_CONSUME_SECONDS = 100_000;

        /// <summary>
        /// Survivor Royale P0에서는 레거시 마니또 체크리스트를 생성하거나 진행하지 않는다.
        /// 데이터와 프로토콜은 보존하므로 레거시 모드가 다시 분리되면 이 게이트로 복구할 수 있다.
        /// </summary>
        public static readonly bool CHECKLIST_SYSTEM_ENABLED = false;
    }
}
