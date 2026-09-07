#pragma warning disable CS8618
// ReSharper disable All
using System.Collections.Generic;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    // ===== RNG 채집 (상자/문 게이지) =====

    /// <summary>
    ///     v0.2.1 (#79) — RNG 채집 결과 통합 패킷. 5종 결과(부품/선행/디코이/빈손/지역 아이템) 단일 응답.
    ///     - 부품/선행 회수 시 추가로 G_TO_C_PART_COLLECTED / G_TO_C_PREREQUISITE_COLLECTED 송신 (인벤토리 갱신용)
    ///     - 본 패킷은 ItemAlert / 시각 이펙트 / 쿨타임 갱신 트리거 전용 (회수자 한정)
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_RNG_COLLECT_RESULT : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        /// <summary>0=빈손, 1=디코이, 2=지역 아이템, 3=부품, 4=선행</summary>
        [Key("resultType")] public int ResultType { get; set; }
        /// <summary>부품/선행/지역 아이템의 식별자 (resultType 0/1은 0). 클라가 csv로 텍스트 조회.</summary>
        [Key("itemId")] public int ItemId { get; set; }
        /// <summary>다음 채집 가능까지 쿨타임 (초). 30초 표준, 0이면 클라 기본값 사용</summary>
        [Key("cooldownSeconds")] public int CooldownSeconds { get; set; }
    }

    /// <summary>
    ///     v0.2.1 (#134) — RNG 채집 인스턴스 쿨타임 broadcast. 매칭 내 모든 클라가 받아 해당 InteractId 마커를
    ///     cooldownSeconds 동안 숨김. 결과(itemId/이름/사유) 정보는 포함 X — 직책 노출 방지 (회수자만 RESULT 받음).
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("cooldownSeconds")] public int CooldownSeconds { get; set; }
    }

    [MessagePackObject]
    public class InteractCooldownSnapshotEntry : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("remainSeconds")] public int RemainSeconds { get; set; }
    }

    /// <summary>
    ///     #137 — 합류/리커넥트 클라이언트용 현재 InteractObject cooldown snapshot.
    ///     실시간 갱신은 G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST가 계속 담당한다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_INTERACT_COOLDOWN_SNAPSHOT : IMessagePackObject
    {
        [Key("entries")] public List<InteractCooldownSnapshotEntry> Entries { get; set; } = new();
    }

    /// <summary>
    ///     RNG 채집 시작 요청. 클릭 시 송신하며 서버가 쿨타임을 등록하고 ACK를 보낸다.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_RNG_COLLECT_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }

    /// <summary>
    ///     #134 — RNG 채집 시작 승인/거부 응답.
    ///     ErrorCode=SUCCESS면 progress 진행 후 FINISH 송신. 거부면 클라가 InteractionPanel 닫음.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_RNG_COLLECT_ACK : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        /// <summary>거부 시 cooldown 남은 초 (이미 회수됨 케이스). 성공 시 0.</summary>
        [Key("cooldownRemainSeconds")] public int CooldownRemainSeconds { get; set; }
    }

    /// <summary>
    ///     #134 — RNG 채집 progress 완료 → 결과 산출 요청. 서버가 RNG 분포로 결과 결정 + RESULT 응답.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_RNG_COLLECT_FINISH : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("encounterCheckOnly")] public bool EncounterCheckOnly { get; set; }
    }
}
