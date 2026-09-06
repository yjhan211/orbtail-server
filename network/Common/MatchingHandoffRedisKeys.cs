using System;

namespace network.common
{
    public static class MatchingHandoffRedisKeys
    {
        public static readonly TimeSpan HandoffStateLifetime = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan PostAdmissionReservationLifetime =
            TimeSpan.FromSeconds(Config.SWARM_MATCH_DURATION_SECONDS) + TimeSpan.FromMinutes(3);
        public static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan AdmissionReservationLifetime = TimeSpan.FromMinutes(2);

        public const string ManifestField = "manifest";
        public const string AdmissionReadyField = "admission_ready";
        public const byte AdmissionReadyValue = 1;
        public const string AdmissionPendingState = "pending";
        public const string AdmissionCompletedState = "completed";
        public const string AdmissionCanceledState = "canceled";
        public const string MatchingHashTag = "{matching}";
        public const string MatchingQueueKey = MatchingHashTag + ":queue";

        public static string Key(long matchingId) => $"matching:{matchingId}:handoff";

        public static string AdmissionStateKey(long matchingId) =>
            $"matching:{matchingId}:admission-state";

        public static string AdmittedPlayerField(long playerId) => $"admitted:{playerId}";

        public static string ReservationKey(long playerId) => $"{MatchingHashTag}:reservation:{playerId}";
    }
}
