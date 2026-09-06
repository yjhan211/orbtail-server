using System;

namespace network.common
{
    public static class MatchingRedisKeys
    {
        public static readonly TimeSpan HandoffStateLifetime = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan PostEntryReservationLifetime =
            TimeSpan.FromSeconds(Config.SWARM_MATCH_DURATION_SECONDS) + TimeSpan.FromMinutes(3);
        public static readonly TimeSpan EntryTimeout = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan EntryReservationLifetime = TimeSpan.FromMinutes(2);

        public const string ManifestField = "manifest";
        // 코드 명칭은 Entry로 통일하되, 기존 Redis 데이터와 호환되도록 저장 문자열은 유지한다.
        public const string EntryReadyField = "admission_ready";
        public const byte EntryReadyValue = 1;
        public const string EntryPendingState = "pending";
        public const string EntryCompletedState = "completed";
        public const string EntryCanceledState = "canceled";
        public const string MatchingIdKey = "matching_id";
        public const string MatchingLeaderKey = "user_server:matching_leader";
        private const string QueueLockKeyPrefix = "matching_queue_lock:";
        public const string MatchingHashTag = "{matching}";
        public const string MatchingQueueKey = MatchingHashTag + ":queue";
        public const string MatchingRequestsKey = MatchingHashTag + ":requests";

        public static string QueueLockKey(long playerId) => QueueLockKeyPrefix + playerId;

        public static string Key(long matchingId) => $"matching:{matchingId}:handoff";

        // 기존 데이터와 같은 키를 읽는다.
        public static string EntryStateKey(long matchingId) =>
            $"matching:{matchingId}:admission-state";

        public static string AdmittedPlayerField(long playerId) => $"admitted:{playerId}";

        public static string ReservationKey(long playerId) => $"{MatchingHashTag}:reservation:{playerId}";
    }
}
