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
        if (IsRoundActionLocked(out _))
        {
            SendMissionNodeExecuteResult(msg.NodeId, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

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
        var node = result.Node;
        var msg = new G_TO_C_MISSION_NODE_EXECUTE_RESULT
        {
            ErrorCode = result.ErrorCode,
            NodeId = node?.NodeId ?? requestedNodeId,
            NodeKey = node?.NodeKey ?? "",
            StoryletId = node?.EffectiveStoryletId ?? "",
            OutputPartId = node?.OutputPartId ?? 0,
            CompletedNodeIds = result.CompletedMissionNodeIds,
            UnlockedNodeIds = result.UnlockedMissionNodeIds,
            ClaimedStoryletIds = result.ClaimedStoryletIds,
            LostStoryletIds = result.LostStoryletIds,
            AlternateRouteNodeIds = result.AlternateRouteNodeIds,
            VisibleTraceTextId = result.VisibleTraceTextId,
            ClaimedByPlayerId = result.ClaimedByPlayerId,
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

    private static List<MissionGraphNodeProgressInfo> BuildMissionGraphNodeProgress(PlayerPartState state)
    {
        lock (state.SyncRoot)
        {
            var availableNodeIds = GameMissionGraphData.GetAvailableNodes(
                    (short)state.JobTitle,
                    state.CollectedParts,
                    state.CompletedMissionNodeIds,
                    state.OwnedClueTags,
                    state.HasLostTarget)
                .Select(node => node.NodeId)
                .ToHashSet();

            return GameMissionGraphData.GetNodes((short)state.JobTitle)
                .Select(node => new MissionGraphNodeProgressInfo
                {
                    NodeId = node.NodeId,
                    NodeKey = node.NodeKey,
                    NodeKind = (int)node.NodeKind,
                    StoryletId = node.EffectiveStoryletId,
                    StoryletType = (int)node.StoryletType,
                    TargetAreaType = node.TargetAreaType,
                    TargetObjectType = node.TargetObjectType,
                    ClaimPolicy = (int)node.ClaimPolicy,
                    RewardKind = (int)node.RewardKind,
                    CaseGroup = node.CaseGroup,
                    IsVictoryStorylet = node.IsVictoryStorylet,
                    IsCompleted = state.CompletedMissionNodeIds.Contains(node.NodeId),
                    IsUnlocked = state.UnlockedMissionNodeIds.Contains(node.NodeId),
                    IsAvailable = availableNodeIds.Contains(node.NodeId),
                    IsDiscovered = !string.IsNullOrWhiteSpace(node.EffectiveStoryletId) &&
                                   state.DiscoveredStoryletIds.Contains(node.EffectiveStoryletId),
                    IsTracked = !string.IsNullOrWhiteSpace(node.EffectiveStoryletId) &&
                                state.TrackedStoryletIds.Contains(node.EffectiveStoryletId),
                    IsClaimed = !string.IsNullOrWhiteSpace(node.EffectiveStoryletId) &&
                                state.ClaimedStoryletIds.Contains(node.EffectiveStoryletId),
                    IsLost = !string.IsNullOrWhiteSpace(node.EffectiveStoryletId) &&
                             state.LostStoryletIds.Contains(node.EffectiveStoryletId)
                })
                .ToList();
        }
    }

    private static List<MissionShortRewardInfo> BuildMissionShortRewardInfoList(PlayerPartState state)
    {
        lock (state.SyncRoot)
        {
            return state.ShortRewards
                .Select(ToShortRewardInfo)
                .ToList();
        }
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
