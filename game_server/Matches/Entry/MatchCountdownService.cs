using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.matches.entry;

/// <summary>
///     매치 입장 마감을 확인하고 시작까지 남은 초를 참가자에게 보낸다.
///     매치 잠금 안에서 마지막으로 보낸 남은 초를 기록하여 같은 값을 중복 전송하지 않는다.
///     입장 시간 초과는 MatchEntryFailureHandler에 위임하며, 타이머 수명은 관리하지 않는다.
/// </summary>
internal sealed class MatchCountdownService(
    MatchRuntimeStore matchRuntimes,
    MatchEntryFailureHandler entryFailureHandler,
    ILogger logger)
{
    public void Broadcast(
        IEnumerable<long> matchingIds,
        IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (long matchingId in matchingIds)
        {
            if (MatchStartGate.IsEntryTimedOut(matchingId, DateTime.UtcNow))
            {
                var anchorSession = activeSessions.FirstOrDefault(
                    session => session.MatchingId == matchingId && session.PlayerId.HasValue);
                if (anchorSession != null)
                {
                    logger.LogWarning(
                        "Match entry deadline expired before every human became ready: MatchingId={MatchingId}",
                        matchingId);
                    entryFailureHandler.Handle(anchorSession);
                }
                continue;
            }

            if (!matchRuntimes.Enter(matchingId, out var scope))
                continue;

            using (scope)
            {
                if (scope.Runtime.IsEnded)
                    continue;

                var snapshot = MatchStartGate.GetSnapshot(matchingId);
                if (!snapshot.IsKnown)
                    continue;

                var pacing = scope.Runtime.Progress;
                if (pacing.LastCountdownSecondsPublished == snapshot.RemainingSeconds)
                    continue;

                pacing.LastCountdownSecondsPublished = snapshot.RemainingSeconds;
                var matchingSessions = activeSessions
                    .Where(session => session.MatchingId == matchingId)
                    .ToList();
                if (matchingSessions.Count == 0)
                    continue;

                using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN);
                packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
                {
                    MatchingId = matchingId,
                    RemainingSeconds = snapshot.RemainingSeconds,
                    ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));

                foreach (var session in matchingSessions)
                    session.TrySend(packet);
            }
        }
    }
}
