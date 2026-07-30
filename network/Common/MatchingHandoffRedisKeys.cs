using System;

namespace network.common
{
    public static class MatchingHandoffRedisKeys
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        public const string BotsField = "bots";

        public static string Key(long matchingId) => $"matching:{matchingId}:handoff";

        public static string SpawnField(long playerId) => $"spawn:{playerId}";
    }
}