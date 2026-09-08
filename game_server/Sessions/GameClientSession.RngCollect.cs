using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private static int SwarmExploreCooldownSeconds => Config.SWARM_EXPLORE_REGEN_SECONDS;
    private Task HandleRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (MatchingId <= 0)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        return RunWithMatchLock(
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

        return RunWithMatchLock(
            () => HandleSwarmRngCollectFinish(msg),
            () =>
            {
                _interactions.TryFinish(msg.InteractId);
                SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            });
    }

    private static bool IsDoorUnlockInteractable(int interactId) => GameInteractableData.Get(interactId) is { DoorId: > 0 };

    private Task HandleSwarmRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (IsEliminated || IsGameEnded)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }
        var result = MatchInteractionService.Start(
            Match, PlayerId!.Value, CurrentArea, msg.InteractId);
        if (result.Error != ErrorCode.SUCCESS)
        {
            SendRngCollectAck(msg.InteractId, result.Error, result.Remaining);
            return Task.CompletedTask;
        }
        _interactions.Begin(msg.InteractId);
        _gameEventLogManager.LogExploreStart(
            MatchingId, PlayerId.Value, msg.InteractId, CurrentArea.ToString(), isBot: false);
        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
        return Task.CompletedTask;
    }

    private Task HandleSwarmRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (!PlayerId.HasValue)
            return Task.CompletedTask;

        // 이전 채집 패킷으로 문 게이지의 시간 검증을 우회할 수 없다.
        if (IsDoorUnlockInteractable(msg.InteractId))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

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

        int dropItemId = MatchInteractionService.OpenBox(
            Match, PlayerId.Value, CurrentArea, msg.InteractId,
            LastValidatedPosition, () => SendSummonStoneState(),
            spawned => GroundItemNotificationService.BroadcastSpawned(Match, CurrentArea, spawned));
        if (dropItemId == 0)
        {
            BroadcastRngCollectCooldown(msg.InteractId, 0);
SendRngCollectResult(msg.InteractId, 0, 0, 0);
            _condition.State = PlayerState.IDLE;
            SendPlayerState();
            return Task.CompletedTask;
        }

        BroadcastRngCollectCooldown(msg.InteractId, SwarmExploreCooldownSeconds);
SendRngCollectResult(msg.InteractId, 0, 0, SwarmExploreCooldownSeconds);
        _condition.State = PlayerState.IDLE;
        SendPlayerState();
        Logger.LogInformation(
            "Swarm box consumable: PlayerId={PlayerId}, InteractId={InteractId}, Cost={Cost}, Drop={DropItemId}",
            PlayerId, msg.InteractId, Config.SWARM_BOX_OPEN_COST, dropItemId);

        return Task.CompletedTask;
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

    private void SendInteractionCanceled(int[] canceledIds, string reason)
    {
        if (canceledIds.Length == 0)
            return;

        foreach (int interactId in canceledIds)
        {
            _gameEventLogManager.LogExploreCancelled(
                MatchingId, PlayerId.GetValueOrDefault(), interactId, CurrentArea.ToString(), reason, isBot: false);
            BroadcastRngCollectCooldown(interactId, 0);
        }

        Logger.LogInformation(
            "Pending interactions cancelled: PlayerId={PlayerId}, Count={Count}, Reason={Reason}",
            PlayerId, canceledIds.Length, reason);
    }

    private void BroadcastRngCollectCooldown(int interactId, int cooldownSeconds)
    {
        var sessions = Match.Sessions.Snapshot();
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

        var snapshot = Volatile.Read(ref _match)?.CollectCooldowns.GetSnapshot();
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
