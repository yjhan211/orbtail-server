using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private Task HandleMissionNodeExecute(C_TO_G_MISSION_NODE_EXECUTE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        if (IsEliminated)
        {
            SendMissionNodeExecuteResult(msg.NodeId, ErrorCode.PLAYER_DEAD);
            return Task.CompletedTask;
        }

        var area = msg.AreaType == AreaType.None ? CurrentArea : msg.AreaType;
        int objectType = msg.ObjectType;

        if (msg.InteractId > 0)
        {
            var interactable = GameInteractableData.Get(msg.InteractId);
            if (interactable == null)
            {
                SendMissionNodeExecuteResult(msg.NodeId, ErrorCode.INTERACTABLE_NOT_FOUND);
                return Task.CompletedTask;
            }

            area = (AreaType)interactable.ZoneId;
            objectType = (int)interactable.ObjectType;
        }

        if (area != CurrentArea)
        {
            SendMissionNodeExecuteResult(msg.NodeId, ErrorCode.AREA_MISMATCH);
            return Task.CompletedTask;
        }

        var result = _missionManager.TryExecuteMissionNode(
            CurrentMapSubId,
            PlayerId.Value,
            msg.NodeId,
            area,
            objectType,
            msg.InteractId,
            msg.ClientStartUnixMs);

        SendMissionNodeExecuteResult(msg.NodeId, result);

        if (!result.Success || result.Node == null)
            return Task.CompletedTask;

        if (msg.InteractId > 0)
            StoreTrace(area, msg.InteractId, result.Node.VisibleTrace.Kr, true);

        _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
            $"미션 노드 실행: {result.Node.Title.Kr} ({result.Node.NodeKey})",
            isBot: false);

        if (result.IsMissionComplete)
        {
            using var allCompletePacket = Packet.Create((int)Protocol.G_TO_C_MISSION_ALL_COMPLETE, PlayerId.Value);
            var allCompleteMsg = new G_TO_C_MISSION_ALL_COMPLETE { JobTitle = MyJobTitle };
            allCompletePacket.SetBody(MessagePackSerializer.Serialize(allCompleteMsg));
            Send(allCompletePacket);

            Logger.LogInformation("미션 그래프 완료: PlayerId={PlayerId}, 직책={Job} — 즉시 게임 종료",
                PlayerId, MyJobTitle);
            EndGameByRaceCompletion(PlayerId.Value);
        }

        return Task.CompletedTask;
    }

    private void SendMissionNodeExecuteResult(int requestedNodeId, MissionNodeExecuteResult result)
    {
        var msg = new G_TO_C_MISSION_NODE_EXECUTE_RESULT
        {
            ErrorCode = result.ErrorCode,
            NodeId = result.Node?.NodeId ?? requestedNodeId,
            NodeKey = result.Node?.NodeKey ?? "",
            OutputPartId = result.Node?.OutputPartId ?? 0,
            CompletedNodeIds = result.CompletedMissionNodeIds,
            UnlockedNodeIds = result.UnlockedMissionNodeIds,
            IsMissionComplete = result.IsMissionComplete,
            GrantedShortReward = ToShortRewardInfo(result.GrantedShortReward)
        };

        SendMissionNodeExecuteResult(msg);
    }

    private void SendMissionNodeExecuteResult(int requestedNodeId, ErrorCode errorCode)
    {
        SendMissionNodeExecuteResult(new G_TO_C_MISSION_NODE_EXECUTE_RESULT
        {
            ErrorCode = errorCode,
            NodeId = requestedNodeId
        });
    }

    private void SendMissionNodeExecuteResult(G_TO_C_MISSION_NODE_EXECUTE_RESULT msg)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_MISSION_NODE_EXECUTE_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private static MissionShortRewardInfo ToShortRewardInfo(MissionShortRewardState? reward)
    {
        if (reward == null) return new MissionShortRewardInfo();

        return new MissionShortRewardInfo
        {
            RewardType = (int)reward.RewardType,
            RemainingUses = reward.RemainingUses,
            ValuePercent = reward.ValuePercent,
            DurationSeconds = reward.DurationSeconds,
            ExpiresAtUnixMs = reward.ExpiresAt.HasValue
                ? new DateTimeOffset(reward.ExpiresAt.Value).ToUnixTimeMilliseconds()
                : 0
        };
    }
}
