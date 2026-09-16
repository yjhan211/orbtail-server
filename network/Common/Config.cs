// ReSharper disable All
using System;
using network.common.data;

namespace network.common
{
    /// <summary>
    ///     네트워크·서버 설정 상수 + 스웜 밸런스 진입점.
    ///     밸런스 스칼라는 swarm_config.csv가 원본(#296) — 프로퍼티가 CSV를 읽고,
    ///     미로드·미등재 키는 코드 기본값(현행값)으로 폴백한다.
    /// </summary>
    public class Config
    {
        public static float SWARM_BOT_WALK_SPEED => SwarmConfigData.GetFloat("SWARM_BOT_WALK_SPEED", 5f);
        public static double SWARM_BOT_WANDER_SCATTER_PROBABILITY => SwarmConfigData.GetDouble("SWARM_BOT_WANDER_SCATTER_PROBABILITY", 0.3d);
        public static double SWARM_BOT_ROOM_DWELL_MIN_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_ROOM_DWELL_MIN_SECONDS", 1.25d);
        public static double SWARM_BOT_ROOM_DWELL_MAX_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_ROOM_DWELL_MAX_SECONDS", 2.25d);

        public static double SWARM_BOT_AREA_RETURN_COOLDOWN_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_AREA_RETURN_COOLDOWN_SECONDS", 5d);
        public static double SWARM_BOT_POST_CUT_LOOT_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_POST_CUT_LOOT_SECONDS", 5d);
        public static int SWARM_BOT_FIELD_EVACUATE_MARGIN_CELLS => SwarmConfigData.GetInt("SWARM_BOT_FIELD_EVACUATE_MARGIN_CELLS", 5);
        public static int SWARM_BOT_MONSTER_HUNT_STOP_DISTANCE_CELLS => SwarmConfigData.GetInt("SWARM_BOT_MONSTER_HUNT_STOP_DISTANCE_CELLS", 5);
        public static int SWARM_BOT_MONSTER_DANGER_RADIUS_CELLS => SwarmConfigData.GetInt("SWARM_BOT_MONSTER_DANGER_RADIUS_CELLS", 10);
        public static int SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS => SwarmConfigData.GetInt("SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS", 6);
        public static double SWARM_BOT_FLEE_COMMIT_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_FLEE_COMMIT_SECONDS", 2d);
        public static int SWARM_BOT_RIVAL_SCAN_RADIUS_CELLS => SwarmConfigData.GetInt("SWARM_BOT_RIVAL_SCAN_RADIUS_CELLS", 22);
        public static int SWARM_BOT_FLEE_PROBE_DISTANCE_CELLS => SwarmConfigData.GetInt("SWARM_BOT_FLEE_PROBE_DISTANCE_CELLS", 16);
        public static int SWARM_BOT_MIN_FLEE_TARGET_DISTANCE_CELLS => SwarmConfigData.GetInt("SWARM_BOT_MIN_FLEE_TARGET_DISTANCE_CELLS", 6);
        public static float SWARM_BOT_FLEE_POWER_RATIO => SwarmConfigData.GetFloat("SWARM_BOT_FLEE_POWER_RATIO", 1.5f);
        public static double SWARM_BOT_DAMAGED_FLEE_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_DAMAGED_FLEE_SECONDS", 6d);
        public static float SWARM_BOT_WOUNDED_ENTER_RATIO => SwarmConfigData.GetFloat("SWARM_BOT_WOUNDED_ENTER_RATIO", 0.4f);
        public static float SWARM_BOT_WOUNDED_EXIT_RATIO => SwarmConfigData.GetFloat("SWARM_BOT_WOUNDED_EXIT_RATIO", 0.55f);
        public static float SWARM_BOT_CUT_MIN_HEALTH_RATIO => SwarmConfigData.GetFloat("SWARM_BOT_CUT_MIN_HEALTH_RATIO", 0.5f);
        public static double SWARM_BOT_CUT_COOLDOWN_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_CUT_COOLDOWN_SECONDS", 6d);

        public static double SWARM_BOT_INITIAL_DECISION_DELAY_MIN_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_INITIAL_DECISION_DELAY_MIN_SECONDS", 0.15d);
        public static double SWARM_BOT_INITIAL_DECISION_DELAY_MAX_SECONDS => SwarmConfigData.GetDouble("SWARM_BOT_INITIAL_DECISION_DELAY_MAX_SECONDS", 1.2d);
        public static int[] SWARM_BOT_DEFAULT_WEAR_ITEM_IDS => SwarmConfigData.GetIntArray("SWARM_BOT_DEFAULT_WEAR_ITEM_IDS", new[] { 101000003, 102000003, 104000005, 105000005, 106000003 });
        public static int[] SWARM_BOT_CUSTOMIZATION_ITEM_IDS => SwarmConfigData.GetIntArray("SWARM_BOT_CUSTOMIZATION_ITEM_IDS", new[] { 103000001, 103000004, 103000005, 103000006 });

        /// <summary>환경 피해 정산 간격(초). 실행 주기와 피해 계산에서 같은 값을 사용한다.</summary>
        public const int ENVIRONMENTAL_TICK_INTERVAL_SECONDS = 5;

        // Network Settings
        /// <summary>최대 동시 연결 수</summary>
        public static readonly int MAX_CONNECTION = 1000;

        /// <summary>소켓 당 미리 할당할 버퍼 개수</summary>
        public static readonly int PRE_ALLOC_COUNT = 2;

        /// <summary>소켓 I/O 버퍼 크기 (바이트). 연결마다 수신·송신 하나씩 pin되므로 작게 유지한다.</summary>
        public static readonly int BUFFER_SIZE = 2048;

        /// <summary>
        ///     메시지 하나의 최대 크기 (헤더 포함, 바이트). I/O 버퍼와 별개다 — 수신 측이 여러 번의 수신을 이어 붙여
        ///     이 크기까지 조립하고, 송신 측은 I/O 버퍼 크기로 잘라 보낸다. 넘는 길이가 오면 그 연결을 끊는다.
        /// </summary>
        public static readonly int MAX_MESSAGE_SIZE = 64 * 1024;

        /// <summary>패킷 헤더 크기 (바이트) - 길이 정보</summary>
        public static readonly int HEADER_SIZE = 4;

        /// <summary>리스닝 소켓 백로그 큐 크기</summary>
        public static readonly int BACK_LOG = 100;

        /// <summary>서버가 accept한 연결이 로그인/게임 인계를 완료해야 하는 제한 시간</summary>
        public const int AUTHENTICATION_TIMEOUT_SECONDS = 15;

        /// <summary>인증된 inbound 연결이 유효 패킷 없이 유지될 수 있는 제한 시간</summary>
        public const int AUTHENTICATED_IDLE_TIMEOUT_SECONDS = 60;

        // Batch Processing Settings
        /// <summary>Redis 배치 작업 크기</summary>
        public static readonly int BATCH_SIZE = 10;

        // Lock Settings
        /// <summary>분산 락 TTL</summary>
        public static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(30);

        // Game Session Settings
        /// <summary>게임 세션 지속 시간 (분)</summary>
        public static readonly int GAME_DURATION_MINUTES = 15;

        /// <summary>Swarm match inventory slot capacity shared by clients, bots, and the game server.</summary>
        public const int LEGACY_INVENTORY_SLOT_COUNT = 6;

        /// <summary>World pickup used to represent one summon stone.</summary>
        public const int SUMMON_STONE_GROUND_ITEM_ID = 107000050;

        public const float GROUND_ITEM_PICKUP_RADIUS = 1.05f;
        public const float SUMMON_STONE_PICKUP_RADIUS = 1.6f;

        // 클라이언트 착지 연출과 서버 획득 대기에 같은 시간을 사용한다.
        public static float GetGroundItemLandingSeconds(float distance) =>
            Math.Max(0.32f, Math.Min(0.55f, 0.3f + distance * 0.08f));

        /// <summary>
        ///     잼 지상 픽업 (#222 M3): 소환석(개봉 재화)과 분리된 승점 재화 — SB의 코인/잼
        ///     이원 구조. 큰 몹·오브 파괴가 떨구고, 잼 최다가 승리를 가른다 (M3 승리 판정).
        /// </summary>
        public const int JAM_GROUND_ITEM_ID = 107000060;

        /// <summary>하트 (#222 M4): 즉시 회복 픽업 — 고위험 몹(탈주·볼러) 처치가 유일 공급처.</summary>
        public const int HEART_GROUND_ITEM_ID = 107000070;

        /// <summary>부츠 (#222 M4): 10초 이속 버프 픽업 — 다트 고블린 드랍. 사람 전용.</summary>
        public const int BOOTS_GROUND_ITEM_ID = 107000080;
        public static int BOOTS_SPEED_DURATION_SECONDS => SwarmConfigData.GetInt("BOOTS_SPEED_DURATION_SECONDS", 10);
        // 1.5: 기본 5의 1.5배 = 7.5, 서버 검증 상한(10) 안 (#222).
        public static float BOOTS_MOVE_SPEED_MULTIPLIER => SwarmConfigData.GetFloat("BOOTS_MOVE_SPEED_MULTIPLIER", 1.5f);

        /// <summary>
        ///     빈손 이속 (#223, SB 정합: 스쿼드를 잃으면 빨라진다): 오브 0개 동안의 이동 배율.
        ///     부츠 중첩 시 5 × 1.3 × 1.5 = 9.75 — 서버 검증 상한(10) 안.
        /// </summary>
        public static float SWARM_BARE_MOVE_SPEED_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_BARE_MOVE_SPEED_MULTIPLIER", 1.3f);

        /// <summary>
        ///     빈손 이속 유지 시간 (#229 12단계). 빈손인 내내 빠르면 "패배 직전"이 아니라
        ///     도주 특화 상태가 된다 — 마지막 오브를 잃은 직후 이 시간만 가속하고 원복한다.
        ///     그 뒤의 빈손은 잔상의 우선 표적이 되어 재건에 쫓긴다.
        /// </summary>
        public static float SWARM_BARE_MOVE_SPEED_SECONDS => SwarmConfigData.GetFloat("SWARM_BARE_MOVE_SPEED_SECONDS", 2f);

        /// <summary>열쇠 (#222 M4): 무료 소환 1회 충전 — 탈주 고블린(미니보스) 드랍. 사람 전용.</summary>
        public const int KEY_GROUND_ITEM_ID = 107000090;

        // 상태 효과 표시 ID (status_effect_info.csv와 동기)
        public const int BOOTS_STATUS_EFFECT_ID = 1101;
        public const int KEY_STATUS_EFFECT_ID = 1102;

        /// <summary>
        ///     무방비 (구 "필사의 탈주"): 오브 0개 상태의 시각화.
        ///     새 능력이 아니라 이미 있는 현상을 읽히게 한 것이다 — 공격·절단 불가에
        ///     잔상 우선 표적까지 걸린 상태이므로, 이름과 설명을 그 규칙으로 갈아 끼운다.
        ///     이속 가속은 2초만 유지되므로(SWARM_BARE_MOVE_SPEED_SECONDS) 더는
        ///     "탈주 버프"가 아니다.
        /// </summary>
        public const int BARE_STATUS_EFFECT_ID = 1103;

        /// <summary>
        ///     필사의 저항: 절단당한 직후 반격 보호 창의 시각화.
        ///     내 꼬리를 자른 상대의 본체 공격만 무효가 된다(#227 7단계) — 제3자·잔상은
        ///     그대로 들어온다. 서버가 잔광 VFX와 같은 시점·지속으로 보낸다.
        /// </summary>
        public const int RETALIATION_STATUS_EFFECT_ID = 1104;

        /// <summary>침수 (#268): 파도 소용돌이 피격 — 5초 이동 감속 디버프.</summary>
        public const int WAVE_SOAKED_STATUS_EFFECT_ID = 1105;

        /// <summary>화상 (#268): 태양 미사일 피격 — 3초 틱 피해 디버프.</summary>
        public const int SUN_BURN_STATUS_EFFECT_ID = 1106;

        /// <summary>상처 (#268): 바람 칼날 피격 — 5초간 치명타 피격 확률 증가 디버프.</summary>
        public const int WIND_WOUND_STATUS_EFFECT_ID = 1107;

        /// <summary>
        ///     보스 사거리 (#223): 파도 T3 오브급(기본 2.5 + 가중치 4 × 0.4) — 제자리 고정
        ///     포대의 위협 반경. 서버 판정(swarm_monster.csv attack_range)과 클라 범위 링이
        ///     이 값을 공유한다 (표시 = 판정) — CSV 보스 행과 동기 필수.
        /// </summary>
        public static float SWARM_BOSS_ATTACK_RANGE => SwarmConfigData.GetFloat("SWARM_BOSS_ATTACK_RANGE", 4.1f);

        /// <summary>격화 2단계 시각(초) — 이후 이속만 소폭 상승</summary>
        public static double SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS", 230d);

        /// <summary>격화 2단계 이속 배율 — 접촉 피해는 불변</summary>
        public static float SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER", 1.1f);

        /// <summary>구역 보충 웨이브 간격(초) — 웨이브 사이가 정리하는 창</summary>
        public static double SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS", 6d);

        /// <summary>웨이브 한 번의 최대 보충 마릿수 — 구역 목표에 잘린다</summary>
        public static int SWARM_MONSTER_SUPPLY_TOP_UP_COUNT => SwarmConfigData.GetInt("SWARM_MONSTER_SUPPLY_TOP_UP_COUNT", 30);

        /// <summary>일반 몹 하트 드롭 확률</summary>
        public static double SWARM_MONSTER_SUPPLY_HEART_DROP_CHANCE => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_HEART_DROP_CHANCE", 0.03d);

        /// <summary>구역 전멸 뒤 보충 휴지(초)</summary>
        public static double SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS", 2d);

        /// <summary>최종 페이즈 전멸 휴지(초) — 0이면 휴지 없음</summary>
        public static double SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS_FINAL_PHASE => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_WIPE_REST_SECONDS_FINAL_PHASE", 0d);

        /// <summary>참가자와 이보다 가까운 앵커에는 제자리 스폰하지 않음</summary>
        public static float SWARM_MONSTER_SUPPLY_SAFE_SPAWN_DISTANCE => SwarmConfigData.GetFloat("SWARM_MONSTER_SUPPLY_SAFE_SPAWN_DISTANCE", 2.5f);

        /// <summary>참가자와 이보다 먼 앵커를 우선 — 화면 밖에서 태어난다</summary>
        public static float SWARM_MONSTER_SUPPLY_OFFSCREEN_DISTANCE => SwarmConfigData.GetFloat("SWARM_MONSTER_SUPPLY_OFFSCREEN_DISTANCE", 7f);

        /// <summary>절차 앵커의 안쪽(운동장 방향) 편향 거리</summary>
        public static float SWARM_MONSTER_SUPPLY_INWARD_BIAS => SwarmConfigData.GetFloat("SWARM_MONSTER_SUPPLY_INWARD_BIAS", 2.2f);

        /// <summary>침투 경로가 막혔을 때 재시도 간격(초)</summary>
        public static double SWARM_MONSTER_SUPPLY_BLOCKED_RETRY_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_BLOCKED_RETRY_SECONDS", 1d);

        /// <summary>핵(탈주 고블린) 한 마리 처치 시 소환석 보상</summary>
        public static int SWARM_MONSTER_SUPPLY_CORE_STONE_REWARD => SwarmConfigData.GetInt("SWARM_MONSTER_SUPPLY_CORE_STONE_REWARD", 3);

        /// <summary>핵이 등장하는 첫 공급 페이즈 인덱스</summary>
        public static int SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX => SwarmConfigData.GetInt("SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX", 2);

        /// <summary>등장 가능 페이즈에서 몬스터 한 마리당 핵 생성 확률</summary>
        public static double SWARM_MONSTER_SUPPLY_CORE_SPAWN_CHANCE => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_CORE_SPAWN_CHANCE", 0.05d);

        /// <summary>매치 전체 생존 몹 상한 — 서버 천장</summary>
        public static int SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP => SwarmConfigData.GetInt("SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP", 420);

        /// <summary>구역 목표 계산에 세는 최대 인원</summary>
        public static int SWARM_MONSTER_SUPPLY_ZONE_CROWD_CAP => SwarmConfigData.GetInt("SWARM_MONSTER_SUPPLY_ZONE_CROWD_CAP", 4);

        /// <summary>선형 인원 초과분의 인당 목표 비율</summary>
        public static double SWARM_MONSTER_SUPPLY_CROWD_EXTRA_RATIO => SwarmConfigData.GetDouble("SWARM_MONSTER_SUPPLY_CROWD_EXTRA_RATIO", 0.5d);

        /// <summary>인당 목표를 그대로 더하는 인원 수 — 그 뒤는 비율 적용</summary>
        public static int SWARM_MONSTER_SUPPLY_CROWD_LINEAR_PLAYERS => SwarmConfigData.GetInt("SWARM_MONSTER_SUPPLY_CROWD_LINEAR_PLAYERS", 2);

        /// <summary>스폰 예고(초) — 이 시간 뒤 활성화</summary>

        /// <summary>무리 산개 반경</summary>
        public static float SWARM_MONSTER_SUPPLY_SCATTER_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_SUPPLY_SCATTER_RADIUS", 1.6f);

        /// <summary>자기장 경계 바깥 스폰 띠 두께(셀)</summary>
        public static int SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS => SwarmConfigData.GetInt("SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS", 6);

        /// <summary>앵커 CSV가 없을 때 방 중심 절차 앵커 반경</summary>
        public static float SWARM_MONSTER_CAMP_ANCHOR_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_CAMP_ANCHOR_RADIUS", 6f);

        /// <summary>운동장 발원 원형 산개 반경</summary>
        public static float SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS", 3.5f);

        /// <summary>발원 각도 지터(라디안, 약 80°)</summary>
        public static float SWARM_MONSTER_INFILTRATION_ORIGIN_JITTER_RADIANS => SwarmConfigData.GetFloat("SWARM_MONSTER_INFILTRATION_ORIGIN_JITTER_RADIANS", 1.3963f);

        /// <summary>발원 뒤 첫 돌출 거리</summary>
        public static float SWARM_MONSTER_INFILTRATION_BURST_DISTANCE => SwarmConfigData.GetFloat("SWARM_MONSTER_INFILTRATION_BURST_DISTANCE", 2.0f);

        /// <summary>행군 레인 수직 오프셋 최대 — 같은 경로가 한 줄로 겹치지 않게</summary>
        public static float SWARM_MONSTER_MARCH_LANE_OFFSET_MAX => SwarmConfigData.GetFloat("SWARM_MONSTER_MARCH_LANE_OFFSET_MAX", 0.4f);

        /// <summary>행군 속도 개체별 지터 비율</summary>
        public static float SWARM_MONSTER_MARCH_SPEED_JITTER => SwarmConfigData.GetFloat("SWARM_MONSTER_MARCH_SPEED_JITTER", 0.1f);

        /// <summary>출발 시각 지터(초)</summary>

        /// <summary>파도 문양 일반 몹 접촉 쿨다운(초) — 스플래시라 길다</summary>
        public static float SWARM_MONSTER_WAVE_INSIGNIA_ATTACK_COOLDOWN_SECONDS => SwarmConfigData.GetFloat("SWARM_MONSTER_WAVE_INSIGNIA_ATTACK_COOLDOWN_SECONDS", 2.2f);

        /// <summary>비점유 구역 잔상 회수 유예(초)</summary>
        public static double SWARM_MONSTER_STRANDED_GRACE_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_STRANDED_GRACE_SECONDS", 6d);

        /// <summary>표적 재탐색 간격(초)</summary>
        public static double SWARM_MONSTER_TARGET_HOLD_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_TARGET_HOLD_SECONDS", 1d);

        /// <summary>잠든 몹의 앵커 주변 순찰 반경</summary>
        public static float SWARM_MONSTER_IDLE_PATROL_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_IDLE_PATROL_RADIUS", 2.2f);

        /// <summary>순찰 각속도(라디안/초)</summary>
        public static double SWARM_MONSTER_IDLE_PATROL_ANGULAR_SPEED => SwarmConfigData.GetDouble("SWARM_MONSTER_IDLE_PATROL_ANGULAR_SPEED", 0.5d);

        /// <summary>원거리 몹이 멈춰 서는 사거리 비율</summary>

        /// <summary>행군 웨이포인트 도착 판정 거리</summary>
        public static float SWARM_MONSTER_MARCH_WAYPOINT_ARRIVE_DISTANCE => SwarmConfigData.GetFloat("SWARM_MONSTER_MARCH_WAYPOINT_ARRIVE_DISTANCE", 0.6f);

        /// <summary>추격 경로 재계획 간격(초)</summary>
        public static double SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS", 0.4d);

        /// <summary>행군 예산 = 경로 소요 시간 × 이 여유</summary>
        public static double SWARM_MONSTER_MARCH_BUDGET_SLACK_MULTIPLIER => SwarmConfigData.GetDouble("SWARM_MONSTER_MARCH_BUDGET_SLACK_MULTIPLIER", 1.8d);

        /// <summary>행군 예산 최소(초)</summary>
        public static double SWARM_MONSTER_MARCH_BUDGET_MINIMUM_SECONDS => SwarmConfigData.GetDouble("SWARM_MONSTER_MARCH_BUDGET_MINIMUM_SECONDS", 8d);

        /// <summary>기본 접촉 반경(해골 몸통 반폭) — 종별 contact_radius_scale의 기준</summary>
        public static float SWARM_MONSTER_BASE_CONTACT_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_BASE_CONTACT_RADIUS", 0.5f);

        /// <summary>잔상 접촉 후 참가자 피격 무적창(초) — 플레이어당 전역</summary>
        public static float SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS => SwarmConfigData.GetFloat("SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS", 0.6f);

        /// <summary>볼러 접촉 스플래시 반경</summary>

        /// <summary>파도 문양 잔상 접촉 스플래시 반경</summary>
        public static float SWARM_MONSTER_WAVE_SPLASH_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_WAVE_SPLASH_RADIUS", 2.2f);

        /// <summary>잔상 근접 개전 반경</summary>
        public static float SWARM_MONSTER_AGGRO_RADIUS => SwarmConfigData.GetFloat("SWARM_MONSTER_AGGRO_RADIUS", 2.5f);

        /// <summary>잔상 이동 속도 (행군·추격 공통)</summary>
        public static float SWARM_MONSTER_MOVE_SPEED => SwarmConfigData.GetFloat("SWARM_MONSTER_MOVE_SPEED", 4.2f);


        /// <summary>파도(Blue) 오브 공급 — 단색 검증이 필요하면 false로 차단한다.</summary>
        public static readonly bool SWARM_WAVE_ORB_ENABLED = true;

        /// <summary>태양(Red) 오브 공급 — 단색 검증이 필요하면 false로 차단한다.</summary>
        public static readonly bool SWARM_SUN_ORB_ENABLED = true;

        /// <summary>
        ///     바람(Green) 오브 공급 — 단색 검증이 필요하면 false로 차단한다.
        ///     세 토글을 전부 끄면 공급 풀이 비므로 최소 하나는 켜 둘 것.
        /// </summary>
        public static readonly bool SWARM_WIND_ORB_ENABLED = true;

        // 오브열 (#226 실험 α/β): 오브가 이동 경로를 따라오는 전투열 — 클라 배치와
        // 서버 판정(오브별 공격 원점·본체 접촉)이 같은 값을 쓴다 (표시 = 판정).
        // 꼬리를 촘촘하게 (#227) — 열 응집감 + 림 메타볼 연결 강화.
        public static float SWARM_ORB_TRAIL_SPACING => SwarmConfigData.GetFloat("SWARM_ORB_TRAIL_SPACING", 0.7f);
        public static float SWARM_ORB_TRAIL_FIRST_OFFSET => SwarmConfigData.GetFloat("SWARM_ORB_TRAIL_FIRST_OFFSET", 0.7f);

        /// <summary>Swarm match combat and closure elimination threshold.</summary>
        public static int MAX_HEALTH => SwarmConfigData.GetInt("MAX_HEALTH", 420);

        /// <summary>
        ///     스웜 아레나 매치 정원. School2 신맵은 1인 시작방 8곳 × 합류 4세트 동심원 구조라 정원 = 시작방 수.
        ///     사람은 항상 1명이고 나머지는 봇으로 채운다.
        /// </summary>
        public static int SWARM_PLAYERS_PER_MATCH => SwarmConfigData.GetInt("SWARM_PLAYERS_PER_MATCH", 8);

        /// <summary>
        ///     #272 매치 맵 단일 원천 — 스웜 매치가 도는 맵. 매치 경로의 모든 맵 참조는
        ///     MapId.School 하드코딩 대신 이 상수를 본다 (School은 데이터·테스트로 보존).
        /// </summary>
        public static readonly MapId SWARM_MATCH_MAP = MapId.School2;

        /// <summary>
        ///     매치 길이 (#226 단계 B) — 5분 오브 점수전. 개전(카운트다운 종료) 앵커 기준이며,
        ///     만료 시 생존자 중 오브 최다 보유자가 승리한다(동점: 티어 합 → 철갑 → 본체 게이지).
        ///     클라 타이머(GameStatusDisplay)와 폐쇄 시간표(AreaClosureManager 최종 웨이브)가
        ///     같은 값에 정렬된다. 잼 승점·4분 잼 타임아웃(#222 M3-2)은 퇴역.
        /// </summary>
        public static int SWARM_MATCH_DURATION_SECONDS => SwarmConfigData.GetInt("SWARM_MATCH_DURATION_SECONDS", 300);

        /// <summary>
        ///     자기장 수축 유예(초) — 개전 후 이 시간 동안 전 맵 안전, 이후 매치 종료까지 안전
        ///     반경이 최대치에서 0으로 선형 수축한다 (종료 시 운동장 중심만 안전). 서버 판정과
        ///     클라 경계 렌더가 같은 값으로 보간한다.
        ///     유예 0: 개전 즉시 매치 전체 길이에 걸쳐 천천히 조인다 — 경계가 처음부터 존재해야 경계 토출 몹
        ///     스폰의 원천이 마르지 않는다.
        /// </summary>
        public static int SWARM_FIELD_HOLD_SECONDS => SwarmConfigData.GetInt("SWARM_FIELD_HOLD_SECONDS", 0);

        /// <summary>
        ///     자기장 수축 곡선 지수 (#272): 1 = 선형, 커질수록 초반 느리고 후반 빠르다 (안전 반경 = Max×(1−진행률^지수)).
        ///     방은 짧게, 운동장은 길게 — 종반을 압축하기로 결정. 서버 판정·클라 경계 렌더·파생 시간표가
        ///     SwarmPressureField의 같은 곡선 함수를 쓴다.
        /// </summary>
        public static double SWARM_FIELD_SHRINK_EXPONENT => SwarmConfigData.GetDouble("SWARM_FIELD_SHRINK_EXPONENT", 1.4d);

        /// <summary>문 게이지 시간(초) — 시작방 문: 혼자 여는 관문이라 짧다. 봇 채널도 같은 값.</summary>
        public static float SWARM_DOOR_GAUGE_SECONDS => SwarmConfigData.GetFloat("SWARM_DOOR_GAUGE_SECONDS", 3f);

        // 합류에서 중앙으로 가는 J 문이 긴 이유 (#272): 선착자도 후착자 도착 전까지 못 나가고, 두 번째 문부터는
        // 피격이 게이지를 리셋하므로 "문을 열려면 상대를 먼저 처리해야 한다"가 규칙에서 나온다. 값은 door_info.csv 저작.

        /// <summary>
        ///     #272 School2 문 등급: 합류→중앙 J 문만 듀얼 관문 게이지(12초), 나머지는 기본(3초).
        ///     문별 값은 door_info.csv gauge_seconds 컬럼이 원본 (#292 CSV 이전) — 0이면 기본값.
        /// </summary>
        public static float GetSwarmDoorGaugeSeconds(int doorId)
        {
            float gaugeSeconds = network.common.data.GameDoorData.Get(doorId)?.GaugeSeconds ?? 0f;
            return gaugeSeconds > 0f ? gaugeSeconds : SWARM_DOOR_GAUGE_SECONDS;
        }

        /// <summary>
        ///     스웜 탐색 스팟 개봉 비용은 SB 상자 문법을 따른다: 스쿼드(궤도 오브)가 클수록
        ///     다음 개봉이 비싸진다. 3머지가 오브 수를 줄이면 비용이 도로 내려간다 —
        ///     슬롯 차단 대신 비용 곡선이 성장을 억제한다. 사람·봇 공통.
        /// </summary>
        // 첫 소환은 싸게 — 상시 쫓기는 판에서 초반이 마르지 않게 하고, 성장 억제는 오브 수 비례 가산이 맡는다.
        public static int SWARM_EXPLORE_COST_BASE => SwarmConfigData.GetInt("SWARM_EXPLORE_COST_BASE", 1);

        /// <summary>스팟 리젠 시간(초). 개봉된 스팟은 사라지지 않고 이 시간 뒤 다시 나온다.
        ///     상자 = 소모품(하트·부츠) 공급처라 즉시 리젠이면 하트가 무한이다.</summary>
        public static int SWARM_EXPLORE_REGEN_SECONDS => SwarmConfigData.GetInt("SWARM_EXPLORE_REGEN_SECONDS", 30);

        /// <summary>
        ///     상자 개봉 비용 (#226 단계 C): 상자는 오브가 아니라 소모품(하트·부츠)을 준다 —
        ///     소환석의 주 소비처는 성장 카드이므로 상자는 고정 저가.
        /// </summary>
        public static int SWARM_BOX_OPEN_COST => SwarmConfigData.GetInt("SWARM_BOX_OPEN_COST", 1);

        /// <summary>
        ///     오브 계열 강화 기본 비용: 해당 계열의 강화 성공 횟수 N 기반 5+2N.
        ///     오브가 잘려도 N은 줄지 않아 절단이 성장 시간을 초기화하지 못한다.
        /// </summary>
        public static int GetSwarmGrowthBaseCost(int growthSuccessCount) =>
            SwarmConfigData.GetInt("SWARM_GROWTH_BASE_COST", 5) +
            SwarmConfigData.GetInt("SWARM_GROWTH_COST_PER_SUCCESS", 2) * Math.Max(0, growthSuccessCount);

        /// <summary>오브 계열 강화의 상한 비용.</summary>
        public static int SWARM_GROWTH_COST_CAP => SwarmConfigData.GetInt("SWARM_GROWTH_COST_CAP", 21);

        /// <summary>궤도 오브 1개당 개봉 비용 가산 — SB "스쿼드 인원수 비례 상자 코인".</summary>
        public static int SWARM_EXPLORE_COST_PER_ORB => SwarmConfigData.GetInt("SWARM_EXPLORE_COST_PER_ORB", 2);

        /// <summary>
        ///     기본가 허용량 — 오브가 이 수 이하면 기본가(5)에서 출발하고, 성장분에만 가산이 붙는다.
        ///     (#219 M2: 시작 오브 지급은 퇴역 — 이 값은 가격 곡선의 피벗으로만 남는다)
        /// </summary>
        public static int SWARM_STARTING_ORB_COUNT => SwarmConfigData.GetInt("SWARM_STARTING_ORB_COUNT", 3);

        /// <summary>
        ///     빈손(오브 0개)은 개봉 무료 — 빈손 시작의 첫 오브와 전멸 후 재기가 같은 경로로 성립한다.
        /// </summary>
        public static int GetSwarmExploreCost(int orbCount) =>
            orbCount <= 0
                ? 0
                : SWARM_EXPLORE_COST_BASE +
                  SWARM_EXPLORE_COST_PER_ORB * Math.Max(0, orbCount - SWARM_STARTING_ORB_COUNT);

        /// <summary>
        ///     오브 꼬리 안전상한 (#232 무한 꼬리): 플레이어에게 보이는 상한은 없고, 소환 비용 곡선이
        ///     성장을 억제한다. 이 값은 정상 5분 매치에서 닿지 않는 이상 상황 방지용 안전장치일 뿐이다.
        ///     클라 꼬리 슬롯 수와 같아야 한다 (PlayerTool.MaxOrbSlots).
        /// </summary>
        // 클라 PlayerTool.MaxOrbSlots(99)와 같아야 한다. 포화 상태·포화 해소 UI는 쓰지 않는다.
        public const int SWARM_ORB_CAPACITY = 99;

        /// <summary>오브 보유 안전상한 — 무한 꼬리라 실질 상한이 아니다 (SWARM_ORB_CAPACITY 주석 참조).</summary>
        public static int GetOrbCapacity() =>
            SWARM_ORB_CAPACITY;

        /// <summary>
        ///     스웜 기본 오브 사거리. 서버 전투(GameServer.SwarmArena)와 클라 사거리 링
        ///     (PlayerRangeRing)이 같은 값을 읽어야 표시와 판정이 일치한다.
        ///     좁게 시작한다 — 사거리 성장의 여지다.
        ///     다트 고블린 사거리(5)의 절반 — 원거리 몹 접근엔 피격 감수가 전제.
        ///     battle_item_combat.csv attack_range(2.5)와 동기 필수 (#292).
        /// </summary>
        public static float SWARM_ORB_ATTACK_RANGE => SwarmConfigData.GetFloat("SWARM_ORB_ATTACK_RANGE", 2.5f);

        /// <summary>
        ///     PvE 오브 사거리(바닥면 단위). 사거리가 구역 전체를 덮으면 후미 절단과 머리 절단의 위험이 같아지므로,
        ///     오브가 자기 열 좌표 주변만 덮는 국소 화망으로 잡는다 — 깊게 자를수록 앞열 오브들의 사거리가 겹치는 자리로 들어가야 한다 (#227).
        /// </summary>
        public static float SWARM_PVE_SAME_AREA_ATTACK_RANGE => SwarmConfigData.GetFloat("SWARM_PVE_SAME_AREA_ATTACK_RANGE", 7f);

        /// <summary>
        ///     오브 궤도 (#232): 오브는 본체 주위 타원 궤도를 돈다 — 이동한 거리만큼 돌고 멈추면 선다.
        ///     위상 = 시드 + 이동 거리 × 도/단위.
        ///     서버가 검증 이동으로 적산해 G_TO_C_MOVE에 실어 보내고(권위), 클라는 자기 트랜스폼 이동으로
        ///     같은 식을 적산하다 그 값으로 보정한다 — 서버는 그 자리를 오브별 발사 원점·표적 선정 기준으로
        ///     쓰고, 클라는 그 자리에 그린다. 9도/단위 = 걷기 속도 6에서 54도/초.
        ///     텔레포트(구역 이동)만큼의 점프는 적산하지 않는다.
        ///     반지름 = 사거리 × 배수, 아이소 타원(y 절반), 궤도 중심 = 본체 + Y 오프셋.
        /// </summary>
        public const float SWARM_ORB_ORBIT_DEGREES_PER_UNIT = 9f;
        public const float SWARM_ORB_ORBIT_TELEPORT_DISTANCE = 3f;
        public const float SWARM_ORB_ORBIT_RADIUS_MULTIPLIER = 0.8f;
        public const float SWARM_ORB_ORBIT_ISO_Y_SCALE = 0.5f;
        public const float SWARM_ORB_ORBIT_CENTER_OFFSET_Y = 0.8f;

        /// <summary>
        ///     유저간 사격 사거리. PvE(7)보다 짧게 — 붙어야 싸운다.
        ///     플레이어 본체 기준으로 잰다: 오브별 원점으로 재면 꼬리가 길수록 사정권이
        ///     늘어나 "오브 수는 PvP 화력을 키우지 않는다"는 규칙과 어긋나고, 링 하나로
        ///     표시할 수도 없다. 클라 표시(PlayerRangeRing)가 같은 값을 읽는다.
        /// </summary>
        public static float SWARM_PVP_ATTACK_RANGE => SwarmConfigData.GetFloat("SWARM_PVP_ATTACK_RANGE", 5f);

        /// <summary>
        ///     유저간 사격에 참여하는 오브 수 = 앞열 이만큼.
        ///     전체 오브가 사람을 쏘면 20개 꼬리가 3개 꼬리를 그대로 녹인다. 상한을 두면
        ///     오브 수는 PvE 성장과 절단 위험만 키우는 축이 된다.
        /// </summary>
        public static int SWARM_PVP_ORB_COUNT => SwarmConfigData.GetInt("SWARM_PVP_ORB_COUNT", 3);

        /// <summary>PvP 피해 1당 본체 체력 피해 환산. 소수부는 피해자별로 이월 누산해 버리지 않는다.</summary>
        public static float SWARM_PVP_DAMAGE_PER_DAMAGE => SwarmConfigData.GetFloat("SWARM_PVP_DAMAGE_PER_DAMAGE", 0.12f);

        // ===== 교차사격 (#232 2단계) =====
        // 오브는 몬스터만 쏜다. 그 공격이 만드는 모양(태양 = 직선)에 다른 플레이어가 들어오면
        // 고정 충격을 받는다. 티어는 모양의 크기만 키우고 충격값은 안 키운다.
        // 서버 판정과 클라 예고 표시가 같은 값을 읽어야 "표시 = 판정"이 성립한다.

        /// <summary>교차사격 충격 1회의 체력 피해. 티어·공격 강화와 무관한 고정값.</summary>
        public static int SWARM_CROSSFIRE_SHOCK_DAMAGE => SwarmConfigData.GetInt("SWARM_CROSSFIRE_SHOCK_DAMAGE", 50);

        /// <summary>
        ///     받는 피해 배율(봇·플레이어 전부 1/3만 받게 결정): 사람·봇 공통,
        ///     PvP 충격(태양·바람·파도)과 잔상 접촉·원거리 피해에 곱한다. 절단 자해(+35)와 폐쇄 즉사는 대상 아님.
        ///     최솟값 1 — 0이 되면 "맞았는데 안 닳는" 피격이 생긴다.
        /// </summary>
        public static float SWARM_DAMAGE_TAKEN_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_DAMAGE_TAKEN_MULTIPLIER", 1f / 3f);

        /// <summary>받는 피해에 배율을 적용한 정수값 — 반올림, 최솟값 1.</summary>
        public static int ScaleSwarmDamageTaken(int damage) =>
            damage <= 0 ? damage : Math.Max(1, (int)Math.Round(damage * SWARM_DAMAGE_TAKEN_MULTIPLIER));

        // 충격 면역은 없다 — 태양 다발 화망에서 첫 발 이후가 소리 없이 관통하면 "안 맞는" 오독이 된다.
        // 지나간 발은 다 맞는다.

        /// <summary>
        ///     화상 (#268): 태양 미사일 충격에 맞으면 3초간 매초 틱 피해 —
        ///     틱당 = 충격의 0.2배(≈3). 재피격 시 지속이 갱신된다(중첩 없음). 몹은 제외 —
        ///     태양 PvE 화력은 이미 직격이 정점이다.
        /// </summary>
        public static float SWARM_SUN_BURN_SECONDS => SwarmConfigData.GetFloat("SWARM_SUN_BURN_SECONDS", 3f);
        public static float SWARM_SUN_BURN_TICK_INTERVAL_SECONDS => SwarmConfigData.GetFloat("SWARM_SUN_BURN_TICK_INTERVAL_SECONDS", 1f);
        public static float SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER", 0.2f);

        /// <summary>
        ///     상처 (#268): 바람 칼날 충격에 맞으면 5초간, 이후 받는 PvP 충격이
        ///     이 확률로 치명타(PvE와 같은 2배)가 된다. 평시 PvP 충격은 치명타가 없다 —
        ///     상처가 그 문을 연다. 재피격 시 지속 갱신(중첩 없음).
        /// </summary>
        public static float SWARM_WIND_WOUND_SECONDS => SwarmConfigData.GetFloat("SWARM_WIND_WOUND_SECONDS", 5f);
        public static float SWARM_WIND_WOUND_CRIT_CHANCE => SwarmConfigData.GetFloat("SWARM_WIND_WOUND_CRIT_CHANCE", 0.35f);

        /// <summary>
        ///     한 플레이어가 동시에 유지할 수 있는 교차사격 예고 수 (명세 "동시 예고 최대 2개"). 예고(시전)
        ///     중인 모양만 센다 — 예고 시간이 0인 지금은 사실상 안 걸리고, 예고를 되살릴 때를 위해 남긴다.
        ///     상한에 닿은 소유자의 태양은 표적을 잡지 않고 기다렸다가(리졸버 필터) 자리가 나면 쏜다 —
        ///     버리지 않는다. 모양 없이 때리던 옛 폴백은 "안 맞은 몹이 죽는" 보이지 않는 피해였다 — 표시 = 판정.
        /// </summary>
        public const int SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER = 2;

        /// <summary>
        ///     태양 투사체: 같은 타일 X/Y축 표적을 향해 큰 구체 하나가 직진하며
        ///     선상의 몬스터·플레이어를 대상당 한 번 관통 타격한다. 벽에서는 피해 없는 시각 폭발,
        ///     벽 없는 끝점에서는 폭발 없이 소멸한다. 예고 시간에는 고정된 시안색 바닥 경로선이
        ///     차오르고, 발사 순간 0.22초 점멸·페이드한 뒤 비행은 꼬리 없는 태양 구체가 전달한다.
        /// </summary>
        public static float SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS => SwarmConfigData.GetFloat("SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS", 0.25f);
        // 서버 앞머리 속도. 클라 투사체는 패킷의 ActiveSeconds(= 실제 벽까지 거리/속도)를 그대로 써
        // 표시와 판정의 도착 시간을 맞춘다.
        public static float SWARM_CROSSFIRE_SUN_SWEEP_SPEED => SwarmConfigData.GetFloat("SWARM_CROSSFIRE_SUN_SWEEP_SPEED", 7.5f);

        /// <summary>
        ///     큰 공격 한 번 = 유도탄 두 발 몫. 주기 ×2, 피해 ×2 — 총 화력은 같고 한 번의 무게가 커진다.
        ///     T1 24는 일반 몹(16~22)을 한 방에 지우고 관통하므로 실측 뒤 조정 대상이다.
        /// </summary>
        public static float SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER", 2f);
        public static float SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER", 2f);

        /// <summary>
        ///     태양 투사체의 판정 폭(T1/T2/T3, 바닥면 단위) — 이 안에 몸이 걸리면 닿은 것.
        ///     "안 맞는" 체감의 원인은 폭이 아니라 세로 축이었고 몸통 캡슐 판정이 그걸 풀었다 — 폭은 넓히지 않는다.
        /// </summary>
        private static readonly float[] DefaultSunWidthByTier = { 0.7f, 0.85f, 1f };
        public static float[] SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER =>
            SwarmConfigData.GetFloatArray("SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER", DefaultSunWidthByTier);

        /// <summary>
        ///     태양 표적 획득 거리(T1/T2/T3, 바닥면 단위). 투사체 길이로는 쓰지 않는다 (투사체는 항상 구역 경계까지
        ///     난다) — 강화는 조준이 걸리는 거리만 늘린다.
        /// </summary>
        private static readonly float[] DefaultSunRangeByTier = { 4f, 5.5f, 7f };
        public static float[] SWARM_CROSSFIRE_SUN_RANGE_BY_TIER =>
            SwarmConfigData.GetFloatArray("SWARM_CROSSFIRE_SUN_RANGE_BY_TIER", DefaultSunRangeByTier);

        /// <summary>
        ///     바람 = 회전 칼날 (#268): 오브가 제자리에서 돌며 반경(티어별, 바닥면) 안 전원을 주기 틱으로 간다 —
        ///     믹서기. 몬스터는 틱 PvE 피해, 소유자 아닌 플레이어는 충격(바람 전용 면역 창). "아무도 없을 때 혼자
        ///     도는 게 이상하다"는 평시 저속 자전, 적 감지 시 가속·발광 연출로 해소한다. 반경에 붙어야 갈리는
        ///     무기라 밀집 실효 화력 상승은 접근 리스크가 값을 치른다.
        /// </summary>
        private static readonly float[] DefaultWindBladeRadiusByTier = { 1.4f, 1.65f, 1.9f };
        public static float[] SWARM_WIND_BLADE_RADIUS_BY_TIER =>
            SwarmConfigData.GetFloatArray("SWARM_WIND_BLADE_RADIUS_BY_TIER", DefaultWindBladeRadiusByTier);
        // 틱 0.35초 × 배율 0.375 = 발당 피해 기준 DPS 유지 — "믹서기에 갈린다"는 잘게 자주 맞아야 읽힌다.
        public static float SWARM_WIND_BLADE_TICK_SECONDS => SwarmConfigData.GetFloat("SWARM_WIND_BLADE_TICK_SECONDS", 0.35f);
        public static float SWARM_WIND_BLADE_DAMAGE_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_WIND_BLADE_DAMAGE_MULTIPLIER", 0.375f);
        // 시동 게이트("회전 한 20퍼는 돼야 데미지"): 표적이 반경에 든 순간부터 이 시간은 피해가 없다 — 클라 감지
        // 폴링(0.15초)+가속 20% 도달(0.09초)에 맞춘 값. 반경이 비면 리셋된다(클라 감속과 대칭).
        public static float SWARM_WIND_BLADE_SPINUP_SECONDS => SwarmConfigData.GetFloat("SWARM_WIND_BLADE_SPINUP_SECONDS", 0.2f);

        /// <summary>
        ///     파도 = 소용돌이 (#268): 파도 오브 각각이 주기(2초)마다 자기 열 위치에 소용돌이를 깐다 — 오브가 곧
        ///     무기 위치(바람 칼날과 같은 문법). 예고(0.65초 림 링) 후 반경 안 전원(몹·플레이어 동일)에게 타격
        ///     피드백 피해와 "침수" 디버프(5초 25% 감속, WAVE_SOAKED_STATUS_EFFECT_ID)를 준다.
        ///     변위(당김·밀침·원 밖 축출)는 쓰지 않기로 결정.
        /// </summary>
        public static float SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER => SwarmConfigData.GetFloat("SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER", 0.25f); // 현행 물폭탄 피해의 1/4


        /// <summary>
        ///     태양 벽 충돌 시각 폭발 크기(T1/T2/T3, 바닥면 단위). 추가 피해·충격 판정은 없다.
        /// </summary>
        private static readonly float[] DefaultSunBlastRadiusByTier = { 1.1f, 1.3f, 1.5f };
        public static float[] SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER =>
            SwarmConfigData.GetFloatArray("SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER", DefaultSunBlastRadiusByTier);

        /// <summary>교차사격 모양 종류 — 패킷·로그·클라 렌더가 공유하는 식별자.</summary>
        public const int SWARM_CROSSFIRE_SHAPE_LINE = 1;

        /// <summary>
        ///     교차사격 폭발 통지 — 같은 패킷(G_TO_C_SUN_ORB_ATTACK)을 재사용한다: EventId = 터진 모양,
        ///     OriginX/Y = 폭발 지점(월드), Width = 폭발 반경(바닥면). 클라는 날아가던 투사체를 그 자리에서 터뜨린다.
        /// </summary>
        public const int SWARM_CROSSFIRE_SHAPE_DETONATE = 2;

        // ===== 6칸 빌드 (#232 4단계) =====
        /// <summary>
        ///     시작 지급 (#232 4단계): 무작위 T1 공격 오브 3개 + 소환석 5. 첫 화력을 들고 시작하고,
        ///     첫 판단은 유지·계열 강화·파괴로 옮긴다.
        /// </summary>
        public static int SWARM_STARTING_ORB_GRANT_COUNT => SwarmConfigData.GetInt("SWARM_STARTING_ORB_GRANT_COUNT", 3);
        public static int SWARM_STARTING_STONE_GRANT => SwarmConfigData.GetInt("SWARM_STARTING_STONE_GRANT", 5);


        /// <summary>결정 패킷 액션 — 클라·서버·로그 공유. TargetItemId에서 OrbGroupId를 판정.</summary>
        public const int ORB_UPGRADE_GROUP = 1;

    }
}
