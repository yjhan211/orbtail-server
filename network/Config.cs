#pragma warning disable CA2211

namespace network
{
    public class Config
    {
        public static readonly int MAX_CONNECTION = 1000;
        public static readonly int PRE_ALLOC_COUNT = 2;
        public static readonly int BUFFER_SIZE = 2048;
        public static readonly int HEADER_SIZE = 4;
        public static readonly int BACK_LOG = 100;
        public static readonly int BATCH_SIZE = 10;

        public static readonly float SPEED = 1f;
        public static readonly float MOVE_ELAPSED_TIME = 0.5f / SPEED;
        public static readonly int MOVE_ELAPSED_MS = (int)(MOVE_ELAPSED_TIME * 1000);

        public static readonly float MOVE_ANIM_ELAPSED_TIME = MOVE_ELAPSED_TIME + 0.4f;
        public static float FILP_LOTATION = 180;

        public static string USER_SERVER_IP = "0.0.0.0";
        public static short USER_SERVER_PORT = 7900;
        public static short GAME_SERVER_PORT = 8000;
        public static int MAX_CHAT_LINE = 2000;
        public static int BROADCAST_CHUNK_SIZE = 150;
        public static int BROADCAST_UNIT = BUFFER_SIZE / BROADCAST_CHUNK_SIZE;
        public static bool HEARTBEAT_ACTIVE = false;
        public static TimeSpan LOCK_TTL = TimeSpan.FromSeconds(30);

        public static string REDIS_CONFIG =
            "redis-cluster-leader.operators:6379,abortConnect=False";
        public static int GAME_SERVER_NUM = 1;
    }
}
