using System;
using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     RNG 채집 2단계 흐름 (#134):
///       1) C_TO_G_RNG_COLLECT_START — RippleMarker 클릭 즉시
///          → stamina 차감 + cooldown 등록(short, 4초) + EXPLORE_START broadcast + cooldown broadcast + ACK
///       2) C_TO_G_RNG_COLLECT_FINISH — progress 1.5~2초 후
///          → RNG 결과 산출 + cooldown 갱신(30초) + EXPLORE_END broadcast + cooldown broadcast + RESULT
///     PendingFinish dict로 START가 미처리된 FINISH 거부 + 부정행위 차단.
/// </summary>
public partial class GameClientSession
{

    // #217 P0-c: 스웜 아레나 탐색 스팟 — 비용·리젠 규칙은 봇과 공유하므로 Config에 있다.
    private const int SwarmExploreCooldownSeconds = Config.SWARM_EXPLORE_REGEN_SECONDS;

    /// <summary>개봉 비용은 장소에 붙는다: 기본가 + 그 스팟의 재개봉 가산.</summary>
    // 개봉 비용 = SB 크기 비례: 현재 궤도 오브 슬롯 수 기준. 장소별 재개봉 가산은 퇴역.
    private int GetSwarmExploreCost() =>
        Config.GetSwarmExploreCost(
            _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId!.Value)
                .GetAllItems().Count);

    /// <summary>START 처리됐으나 FINISH 대기 중인 InteractId — 매칭 단위 추적.
    /// FINISH 도착 시 이 set에 있어야 결과 산출 진행.</summary>
    private readonly HashSet<int> _pendingFinish = new();

    private Task HandleRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        Task result = Task.CompletedTask;
        bool executed = _executeMatchRuntime(
            CurrentMapSubId,
            () => result = HandleSwarmRngCollectStart(msg));
        if (!executed)
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
        return result;
    }

    private Task HandleRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        Task result = Task.CompletedTask;
        bool executed = _executeMatchRuntime(
            CurrentMapSubId,
            () => result = HandleSwarmRngCollectFinish(msg));
        if (!executed)
        {
            _pendingFinish.Remove(msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
        }
        return result;
    }

    /// <summary>
    ///     같은 영역 모든 클라(본인 포함)에 G_TO_C_PLAYER_STATE broadcast.
    /// </summary>
    private void BroadcastPlayerState(PlayerState state)
    {
        if (!PlayerId.HasValue) return;
        SetMovementLockState(state == PlayerState.EXPLORE_1);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, state);
        foreach (var session in sameAreaSessions) session.Send(packet);
    }

    /// <summary>
    ///     #217 P0-c: 스웜 아레나에서 탐색 오브젝트는 소환석 5개짜리 오브 드래프트 상자다.
    ///     기존 자동탐색 UX(접근 → 게이지 → 완료)를 그대로 쓰고, 완료 시 오브를 소환한다.
    ///     스태미나·미션·선물·조우 등 레거시 채집 결과는 사용하지 않는다.
    /// </summary>
    /// <summary>
    ///     문 잠금해제 오브젝트인가 (#229). door_id가 붙은 행만 스웜에서 살아 있다.
    /// </summary>
    private static bool IsDoorUnlockInteractable(int interactId) =>
        GameInteractableData.Get(interactId) is { DoorId: > 0 };

    private Task HandleSwarmRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (IsEliminated || IsGameEnded)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        // #229 5단계: 스웜 상자 탐색은 중단 상태다. 문 잠금해제만 예외로 통과시킨다 —
        // 방을 여는 유일한 수단이라 이게 막히면 폐쇄에 갇힌다.
        if (Config.IsSwarmExploreDisabled() && info.DoorId <= 0)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        if (info.ZoneId != (int)CurrentArea)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.AREA_MISMATCH, 0);
            return Task.CompletedTask;
        }

        if (info.DoorId > 0)
            return HandleSwarmDoorUnlockStart(msg.InteractId, info.DoorId);

        // 소환석 부족이면 게이지를 시작하지 않는다 — 헛 채널 방지.
        // #226 단계 C: 상자 = 소모품 공급처(고정 저가) — 궤도 포화 게이트는 오브를 안 주므로 퇴역.
        if (_summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId!.Value).StoneCount <
            Config.SWARM_BOX_OPEN_COST)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INSUFFICIENT_CURRENCY, 0);
            return Task.CompletedTask;
        }

        if (!RngCollectCooldownStore.TryAcquireCooldown(
                CurrentMapSubId, msg.InteractId, RngCollectCooldownStore.DefaultCooldownSeconds,
                out int remaining))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.ACTION_ALREADY_EXPLORED, remaining);
            return Task.CompletedTask;
        }

        _pendingFinish.Add(msg.InteractId);
        _gameEventLogManager.LogExploreStart(
            CurrentMapSubId, PlayerId.Value, msg.InteractId, CurrentArea.ToString(), isBot: false);
        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);

        // 개봉 소음 — 주변 스웜이 개봉자에게 몰린다. 게이지가 곧 리스크 창.
        SwarmExploreNoiseCallback?.Invoke(CurrentMapSubId, PlayerId.Value);
        return Task.CompletedTask;
    }

    private Task HandleSwarmRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (!PlayerId.HasValue)
            return Task.CompletedTask;

        if (msg.EncounterCheckOnly)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
            return Task.CompletedTask;
        }

        // 탈락 후 도착한 FINISH가 소환에 성공하면 드랍된 인벤토리와 상태가 꼬인다
        if (IsEliminated || IsGameEnded)
        {
            _pendingFinish.Remove(msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (!_pendingFinish.Remove(msg.InteractId))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        if (GameInteractableData.Get(msg.InteractId) is { DoorId: > 0 } doorInfo)
            return HandleSwarmDoorUnlockFinish(msg.InteractId, doorInfo.DoorId);

        // #226 단계 C: 상자 = 소모품 공급처 — 개봉하면 하트·부츠가 바닥에 터져 나온다.
        // 오브 성장은 소환석 임계의 성장 카드 3택이 맡는다 (상자 개방 트리거·자동 소환 퇴역).
        if (!_summonStoneManager.TrySpendStones(
                CurrentMapSubId, PlayerId.Value, Config.SWARM_BOX_OPEN_COST, out _))
        {
            // 석 부족 — 쿨다운을 풀어 나중에 다시 열 수 있게 한다.
            RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, msg.InteractId);
            BroadcastRngCollectCooldown(msg.InteractId, 0);
            SendRngCollectResult(msg.InteractId, 0, 0, 0, 0);
            BroadcastPlayerState(PlayerState.IDLE);
            return Task.CompletedTask;
        }

        SendSummonStoneState();
        // 드롭 테이블: 하트 60 / 부츠 40 — 즉시 회복과 기동이 상자의 정체성이다.
        int dropItemId = Random.Shared.Next(100) < 60
            ? Config.HEART_GROUND_ITEM_ID
            : Config.BOOTS_GROUND_ITEM_ID;
        var dropAnchor = LastValidatedPosition;
        if (dropAnchor != null)
        {
            var spawned = _groundItemManager.SpawnItems(
                CurrentMapSubId, CurrentArea, dropAnchor.X, dropAnchor.Y, [dropItemId],
                mapId: Config.SWARM_MATCH_MAP,
                layout: GroundItemSpawnLayout.EliminationScatter);
            BroadcastGroundItemsSpawned(CurrentArea, spawned);
        }

        // 스팟은 소진되지 않는다 — 리젠 시간 뒤 다시 나온다.
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, msg.InteractId);
        RngCollectCooldownStore.TryAcquireCooldown(
            CurrentMapSubId, msg.InteractId, SwarmExploreCooldownSeconds, out _);
        BroadcastRngCollectCooldown(msg.InteractId, SwarmExploreCooldownSeconds);
        SendRngCollectResult(msg.InteractId, 0, 0, 0, SwarmExploreCooldownSeconds);
        BroadcastPlayerState(PlayerState.IDLE);
        Logger.LogInformation(
            "Swarm box consumable: PlayerId={PlayerId}, InteractId={InteractId}, Cost={Cost}, Drop={DropItemId}",
            PlayerId, msg.InteractId, Config.SWARM_BOX_OPEN_COST, dropItemId);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     문 잠금해제 게이지 시작 (#229). 소환석을 받지 않는다 — 탈출은 성장의 결과지 구매가 아니다.
    ///     대신 맞으면 풀린다: 문 앞을 비울 화력이 곧 탈출 조건이다.
    /// </summary>
    private Task HandleSwarmDoorUnlockStart(int interactId, int doorId)
    {
        if (_doorStateManager.IsDoorOpen(CurrentMapSubId, doorId))
        {
            SendRngCollectAck(interactId, ErrorCode.DOOR_ALREADY_OPEN, 0);
            return Task.CompletedTask;
        }

        // 폐쇄된 구역의 문은 밖에서는 열리지 않는다 — 폐쇄 잠금이 게이지보다 위다.
        // 단 내가 그 폐쇄 구역 안에 있으면 연다 (2026-08-18 유저 결정: 갇히면 틱 오염을 받으며 문을 따고 나간다).
        var door = GameDoorData.Get(doorId);
        if (door != null && _areaClosureManager != null &&
            (_areaClosureManager.IsAreaClosed(CurrentMapSubId, door.AreaType) ||
             _areaClosureManager.IsAreaClosed(CurrentMapSubId, door.AreaTypeB)) &&
            !_areaClosureManager.IsAreaClosed(CurrentMapSubId, CurrentArea))
        {
            SendRngCollectAck(interactId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        _pendingFinish.Add(interactId);
        _pendingDoorUnlockInteractId = interactId;
        _gameEventLogManager.LogExploreStart(
            CurrentMapSubId, PlayerId!.Value, interactId, CurrentArea.ToString(), isBot: false);
        SendRngCollectAck(interactId, ErrorCode.SUCCESS, 0);

        // 문을 흔드는 소음 — 주변 잔상이 몰린다. 게이지가 곧 리스크 창이다.
        SwarmExploreNoiseCallback?.Invoke(CurrentMapSubId, PlayerId.Value);
        return Task.CompletedTask;
    }

    private Task HandleSwarmDoorUnlockFinish(int interactId, int doorId)
    {
        _pendingDoorUnlockInteractId = null;
        _swarmDoorUnlockCount++;

        if (!_doorStateManager.OpenDoor(CurrentMapSubId, doorId))
        {
            SendRngCollectResult(interactId, 0, 0, 0, 0);
            BroadcastPlayerState(PlayerState.IDLE);
            return Task.CompletedTask;
        }

        Logger.LogInformation(
            "Swarm door unlocked by gauge: PlayerId={PlayerId}, DoorId={DoorId}, InteractId={InteractId}",
            PlayerId, doorId, interactId);

        using var updatePacket =
            PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId!.Value);
        foreach (var session in _getSessionsByInstance(CurrentMapId, CurrentMapSubId))
            session.Send(updatePacket);

        SendRngCollectResult(interactId, 0, 0, 0, 0);
        BroadcastPlayerState(PlayerState.IDLE);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     피격으로 문 게이지를 끊는다 (#229). 서버가 pending을 지우면 뒤늦게 온 FINISH도 무효가 된다.
    ///     단 첫 문은 끊지 않는다 (2026-08-16 유저 판정): 침투로 몹이 상시 붙게 되면서 시작 화력으로는
    ///     3초를 비울 수 없어 시작 구역에서 아예 못 나가는 상태가 됐다. 첫 문은 성장 이전의 관문이라
    ///     화력을 조건으로 걸 수 없다 — 두 번째 문부터 리스크 창이 돌아온다.
    /// </summary>
    internal void BreakDoorUnlockGauge()
    {
        if (_pendingDoorUnlockInteractId is not { } interactId) return;
        if (_swarmDoorUnlockCount == 0) return;

        _pendingDoorUnlockInteractId = null;
        _pendingFinish.Remove(interactId);
        _gameEventLogManager.LogExploreCancelled(
            CurrentMapSubId, PlayerId ?? 0, interactId, CurrentArea.ToString(), "door_unlock_hit",
            isBot: false);
        SendRngCollectAck(interactId, ErrorCode.INVALID_GAME_STATE, 0);
    }

    private void SendRngCollectAck(int interactId, ErrorCode errorCode, int cooldownRemain)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_RNG_COLLECT_ACK
        {
            InteractId = interactId,
            ErrorCode = errorCode,
            CooldownRemainSeconds = cooldownRemain
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_ACK, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private void CancelPendingRngCollect(string reason)
    {
        if (_pendingFinish.Count == 0)
            return;

        foreach (int interactId in _pendingFinish.ToArray())
        {
            _gameEventLogManager.LogExploreCancelled(
                CurrentMapSubId, PlayerId.GetValueOrDefault(), interactId, CurrentArea.ToString(), reason, isBot: false);
            RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
            BroadcastRngCollectCooldown(interactId, 0);
        }

        Logger.LogInformation(
            "RNG collect pending cancelled: PlayerId={PlayerId}, Count={Count}, Reason={Reason}",
            PlayerId, _pendingFinish.Count, reason);
        _pendingFinish.Clear();
    }

    private void BroadcastRngCollectCooldown(int interactId, int cooldownSeconds)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var msg = new G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST
        {
            InteractId = interactId,
            CooldownSeconds = cooldownSeconds
        };
        var body = MessagePackSerializer.Serialize(msg);
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue) continue;
            using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST, session.PlayerId.Value);
            packet.SetBody(body);
            session.Send(packet);
        }
    }

    private void SendInteractCooldownSnapshot()
    {
        if (!PlayerId.HasValue) return;

        var snapshot = RngCollectCooldownStore.GetSnapshot(CurrentMapSubId);
        if (snapshot.Count == 0) return;

        var msg = new G_TO_C_INTERACT_COOLDOWN_SNAPSHOT
        {
            Entries = snapshot
                .Select(entry => new InteractCooldownSnapshotEntry
                {
                    InteractId = entry.InteractId,
                    RemainSeconds = entry.RemainingSeconds
                })
                .ToList()
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACT_COOLDOWN_SNAPSHOT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);

        Logger.LogInformation("Interact cooldown snapshot sent: PlayerId={PlayerId}, Count={Count}",
            PlayerId.Value, msg.Entries.Count);
    }

    private void SendRngCollectResult(int interactId, int resultType, int itemId,
        int staminaReward, int cooldownSeconds)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_RNG_COLLECT_RESULT
        {
            InteractId = interactId,
            ResultType = resultType,
            ItemId = itemId,
            StaminaReward = staminaReward,
            CooldownSeconds = cooldownSeconds
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }
}
