using System;
using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

/// <summary>
///     프레즌스 (차기 재사용 보존, PRESENCE_SYSTEM_ENABLED 동결): 북마크·날카로운 시선·타겟 위치·기척 카드·노트북 전송.
/// </summary>
public partial class GameClientSession
{
    private Task HandleBookmarkPresence(C_TO_G_BOOKMARK_PRESENCE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Round settlement in progress");
            return Task.CompletedTask;
        }

        long previousBookmarkPlayerId = PresenceBookmarkPlayerId;
        var myWatcher = _matchRosterManager.FindWatcherOf(CurrentMapSubId, PlayerId.Value);
        long newBookmarkPlayerId = msg.TargetPlayerId;
        if (previousBookmarkPlayerId != 0 && previousBookmarkPlayerId != newBookmarkPlayerId &&
            myWatcher?.PlayerId == previousBookmarkPlayerId)
        {
            SendSharpGazeMarkUpdate(previousBookmarkPlayerId, false);
        }

        bool shouldConsumeActivationCost =
            ShouldConsumePresenceBookmarkActivationCost(previousBookmarkPlayerId, newBookmarkPlayerId);
        PresenceBookmarkPlayerId = newBookmarkPlayerId;
        bool isWatcher = PresenceBookmarkPlayerId != 0 && myWatcher?.PlayerId == PresenceBookmarkPlayerId;

        using var packet = Packet.Create((int)Protocol.G_TO_C_BOOKMARK_PRESENCE_RESULT, PlayerId.Value);
        var result = new G_TO_C_BOOKMARK_PRESENCE_RESULT
        {
            TargetPlayerId = PresenceBookmarkPlayerId
        };
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        // 켤 때(새로 켜거나 대상 변경) 1회 스태미나 소모. 끄기는 무료지만 재진입이 비싸 스팸/마이크로 토글을 막는다.
        // 맞든 틀리든 동일 소모라 정답을 누설하지 않고, 스태미나 고갈 시 ModifyStats가 정신력으로 1:2 전환한다.
        if (shouldConsumeActivationCost) ConsumePresenceBookmarkActivationCost(PresenceBookmarkPlayerId);

        if (isWatcher) SendSharpGazeMarkUpdate(PresenceBookmarkPlayerId, true);

        Logger.LogInformation(
            "Presence bookmark updated: PlayerId={PlayerId}, Bookmark={Bookmark}, IsManitto={IsManitto}",
            PlayerId, PresenceBookmarkPlayerId, isWatcher);

        return Task.CompletedTask;
    }

    private static bool ShouldConsumePresenceBookmarkActivationCost(long previousBookmarkPlayerId,
        long newBookmarkPlayerId)
    {
        if (newBookmarkPlayerId == 0) return false;
        return newBookmarkPlayerId != previousBookmarkPlayerId;
    }

    private void ConsumePresenceBookmarkActivationCost(long bookmarkPlayerId)
    {
        ModifyStats(staminaDelta: -BookmarkActivationStaminaCost);
        Logger.LogInformation(
            "Presence bookmark activation cost consumed: PlayerId={PlayerId}, Bookmark={Bookmark}, StaminaCost={Cost}",
            PlayerId, bookmarkPlayerId, BookmarkActivationStaminaCost);
    }

    private void SendSharpGazeMarkUpdate(long targetPlayerId, bool isActive)
    {
        if (targetPlayerId == 0) return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == targetPlayerId);
        if (targetSession == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SHARP_GAZE_MARK_UPDATE, targetPlayerId);
        var msg = new G_TO_C_SHARP_GAZE_MARK_UPDATE { IsActive = isActive };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        targetSession.Send(packet);
    }

    /// <summary>
    ///     마니또 → 타겟 구역 위치 전송
    /// </summary>
    public void SendTargetLocation()
    {
        if (!PlayerId.HasValue || IsEliminated || TargetPlayerId == 0) return;
        // 1인 매칭으로 본인이 본인을 타겟으로 가지는 케이스 방어
        if (TargetPlayerId == PlayerId.Value) return;

        AreaType targetArea;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == TargetPlayerId);
        if (targetSession != null)
        {
            if (targetSession.IsEliminated) return;
            targetArea = targetSession.CurrentArea;
        }
        else
        {
            // #26: 타겟이 봇인 경우 BotPlayerManager에서 위치 조회
            var bot = _botPlayerManager.GetBot(CurrentMapSubId, TargetPlayerId);
            if (bot == null || bot.IsEliminated || bot.PlayerMatchStatus == PlayerMatchStatus.SPECTATING) return;
            targetArea = bot.CurrentArea;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_TARGET_LOCATION, PlayerId.Value);
        var msg = new G_TO_C_TARGET_LOCATION
        {
            TargetPlayerId = TargetPlayerId,
            AreaType = targetArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
        Logger.LogDebug("Target location sent: MatchingId={MatchingId}, PlayerId={PlayerId}, Target={TargetPlayerId}, Area={Area}",
            CurrentMapSubId, PlayerId, TargetPlayerId, targetArea);
    }

    /// <summary>
    ///     프로토 0 기척 갱신 (#159) — 타겟 제외 후보별 최근 25초 조우 강도(0~5) 전송
    /// </summary>
    public void SendPresenceUpdate(List<(long playerId, float presence, string name, List<int> wear)> candidates)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_PRESENCE_UPDATE, PlayerId.Value);
        var msg = new G_TO_C_PRESENCE_UPDATE
        {
            Candidates = candidates
                .Select(c => new PresenceCandidate
                {
                    PlayerId = c.playerId,
                    Presence = c.presence,
                    Name = c.name,
                    WearItemIds = c.wear
                })
                .ToList()
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>Send accumulated presence records for the student notebook.</summary>
    public void SendPresenceNotebookUpdate(long matchingId, int roundNumber, List<PresenceNotebookRecord> records)
    {
        if (!PlayerId.HasValue) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_PRESENCE_NOTEBOOK_UPDATE, PlayerId.Value);
        var msg = new G_TO_C_PRESENCE_NOTEBOOK_UPDATE
        {
            MatchingId = matchingId,
            RoundNumber = roundNumber,
            Entries = records
                .Select(r => new PresenceNotebookEntry
                {
                    PlayerId = r.PlayerId,
                    LastSeenArea = r.LastSeenArea,
                    TotalOverlapSeconds = r.TotalOverlapSeconds,
                    LongestOverlapSeconds = r.LongestOverlapSeconds,
                    CurrentOverlapSeconds = r.CurrentOverlapSeconds,
                    OverlapStartCount = r.OverlapStartCount,
                    EnterAfterObserverCount = r.EnterAfterObserverCount,
                    AlreadyThereWhenObserverArrivedCount = r.AlreadyThereWhenObserverArrivedCount,
                    UnclassifiedOverlapStartCount = r.UnclassifiedOverlapStartCount,
                    IsCurrentlyOverlapping = r.IsCurrentlyOverlapping
                })
                .ToList()
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }
}
