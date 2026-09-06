namespace network.common
{
    public static class MatchingLifecycleSubjects
    {
        public const string PlayerLeft = "matching.player.left";
        public const string PlayerCompleted = "matching.player.completed";
        // 기존 서버와 통신할 수 있도록 subject 문자열은 유지한다.
        public const string PlayerEntryFailed = "matching.player.admission_failed";
        public const string PlayerReleased = "matching.player.released";
    }
}
