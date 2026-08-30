namespace network.common
{
    public static class MatchingLifecycleSubjects
    {
        public const string Stream = "MATCHING_LIFECYCLE";
        public const string AllPlayerEvents = "matching.player.*";
        public const string UserServerDurable = "user-matching-lifecycle-v1";
        public const string UserServerQueue = "user-matching-lifecycle-workers";
        public const string UserServerDeliverSubject = "_delivery.matching.lifecycle.v1";
        public const string PlayerLeft = "matching.player.left";
        public const string PlayerCompleted = "matching.player.completed";
        public const string PlayerAdmissionFailed = "matching.player.admission_failed";
        public const string PlayerReleased = "matching.player.released";
    }
}
