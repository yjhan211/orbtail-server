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

namespace game_server.sessions;

/// <summary>
///     상자·문 요청의 START/FINISH를 받아 세션 대기 상태와 응답을 연결한다.
///     비용·보상·문 판정은 MatchInteractionService, 대기 권리는 PlayerInteractionState가 담당한다.
/// </summary>
public partial class GameClientSession
{

    // #217 P0-c: 스웜 아레나 탐색 스팟 — 비용·리젠 규칙은 봇과 공유하므로 Config에 있다.
    private static int SwarmExploreCooldownSeconds => Config.SWARM_EXPLORE_REGEN_SECONDS;

    /// <summary>개봉 비용은 장소에 붙는다: 기본가 + 그 스팟의 재개봉 가산.</summary>
    // 개봉 비용 = SB 크기 비례: 현재 궤도 오브 슬롯 수 기준. 장소별 재개봉 가산은 퇴역.
    private int GetSwarmExploreCost() =>
        Config.GetSwarmExploreCost(
            _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId!.Value)
                .GetAllItems().Count);

    private Task HandleRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (MatchingId <= 0)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        return RunUnderMatch(
            () => HandleSwarmRngCollectStart(msg),
            () => SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0));
    }

    private Task HandleRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (MatchingId <= 0)
        {
            _interactions.TryFinish(msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        return RunUnderMatch(
            () => HandleSwarmRngCollectFinish(msg),
            () =>
            {
                _interactions.TryFinish(msg.InteractId);
                SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            });
    }

    /// <summary>
    ///     같은 영역 모든 클라(본인 포함)에 G_TO_C_PLAYER_STATE broadcast.
    /// </summary>
    private void BroadcastPlayerState(PlayerState state)
    {
        if (!PlayerId.HasValue) return;
        CurrentState = state;

        var allSessions = _getSessionsByInstance(CurrentMapId, MatchingId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, state);
        foreach (var session in sameAreaSessions) session.TrySend(packet);
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
        var result = MatchInteractionService.Start(
            _matchRuntimes.GetRequired(MatchingId), PlayerId!.Value, CurrentArea, msg.InteractId);
        if (result.Error != ErrorCode.SUCCESS)
        {
            SendRngCollectAck(msg.InteractId, result.Error, result.Remaining);
            return Task.CompletedTask;
        }
        if (result.Door) _interactions.BeginDoor(msg.InteractId);
        else _interactions.Begin(msg.InteractId);
        _gameEventLogManager.LogExploreStart(
            MatchingId, PlayerId.Value, msg.InteractId, CurrentArea.ToString(), isBot: false);
        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
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
            _interactions.TryFinish(msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (!_interactions.TryFinish(msg.InteractId))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        if (GameInteractableData.Get(msg.InteractId) is { DoorId: > 0 } doorInfo)
            return HandleSwarmDoorUnlockFinish(msg.InteractId, doorInfo.DoorId);

        int dropItemId = MatchInteractionService.OpenBox(
            _matchRuntimes.GetRequired(MatchingId), PlayerId.Value, CurrentArea, msg.InteractId,
            LastValidatedPosition, () => SendSummonStoneState(),
            spawned => BroadcastGroundItemsSpawned(CurrentArea, spawned));
        if (dropItemId == 0)
        {
            BroadcastRngCollectCooldown(msg.InteractId, 0);
SendRngCollectResult(msg.InteractId, 0, 0, 0);
            BroadcastPlayerState(PlayerState.IDLE);
            return Task.CompletedTask;
        }

        BroadcastRngCollectCooldown(msg.InteractId, SwarmExploreCooldownSeconds);
SendRngCollectResult(msg.InteractId, 0, 0, SwarmExploreCooldownSeconds);
        BroadcastPlayerState(PlayerState.IDLE);
        Logger.LogInformation(
            "Swarm box consumable: PlayerId={PlayerId}, InteractId={InteractId}, Cost={Cost}, Drop={DropItemId}",
            PlayerId, msg.InteractId, Config.SWARM_BOX_OPEN_COST, dropItemId);

        return Task.CompletedTask;
    }

    /// <summary>승인된 문 게이지 완료 결과를 같은 매치에 전송한다.</summary>

    private Task HandleSwarmDoorUnlockFinish(int interactId, int doorId)
    {
        if (!MatchInteractionService.FinishDoor(_matchRuntimes.GetRequired(MatchingId), _interactions, doorId))
        {
SendRngCollectResult(interactId, 0, 0, 0);
            BroadcastPlayerState(PlayerState.IDLE);
            return Task.CompletedTask;
        }

        Logger.LogInformation(
            "Swarm door unlocked by gauge: PlayerId={PlayerId}, DoorId={DoorId}, InteractId={InteractId}",
            PlayerId, doorId, interactId);

        using var updatePacket =
            PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId!.Value);
        foreach (var session in _getSessionsByInstance(CurrentMapId, MatchingId))
            session.TrySend(updatePacket);

SendRngCollectResult(interactId, 0, 0, 0);
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
        if (_interactions.InterruptDoor() is not { } interactId) return;
        _gameEventLogManager.LogExploreCancelled(
            MatchingId, PlayerId ?? 0, interactId, CurrentArea.ToString(), "door_unlock_hit",
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
        TrySend(packet);
    }

    private void CancelPendingRngCollect(string reason)
    {
        if (_interactions.Count == 0)
            return;

        foreach (int interactId in _interactions.Snapshot())
        {
            _gameEventLogManager.LogExploreCancelled(
                MatchingId, PlayerId.GetValueOrDefault(), interactId, CurrentArea.ToString(), reason, isBot: false);
            _matchRuntimes.Get(MatchingId)?.CollectCooldowns.ClearCooldown(interactId);
            BroadcastRngCollectCooldown(interactId, 0);
        }

        Logger.LogInformation(
            "RNG collect pending cancelled: PlayerId={PlayerId}, Count={Count}, Reason={Reason}",
            PlayerId, _interactions.Count, reason);
        _interactions.Clear();
    }

    private void BroadcastRngCollectCooldown(int interactId, int cooldownSeconds)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, MatchingId);
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
            session.TrySend(packet);
        }
    }

    private void SendInteractCooldownSnapshot()
    {
        if (!PlayerId.HasValue) return;

        var snapshot = _matchRuntimes.Get(MatchingId)?.CollectCooldowns.GetSnapshot();
        if (snapshot == null || snapshot.Count == 0) return;

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
        TrySend(packet);

        Logger.LogInformation("Interact cooldown snapshot sent: PlayerId={PlayerId}, Count={Count}",
            PlayerId.Value, msg.Entries.Count);
    }

    private void SendRngCollectResult(int interactId, int resultType, int itemId,
        int cooldownSeconds)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_RNG_COLLECT_RESULT
        {
            InteractId = interactId,
            ResultType = resultType,
            ItemId = itemId,
            CooldownSeconds = cooldownSeconds
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        TrySend(packet);
    }
}
