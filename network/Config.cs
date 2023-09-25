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

        public static readonly float SPEED = 1f;
        public static readonly float MOVE_ELAPSED_TIME = 0.5f / SPEED;
        public static readonly float MOVE_ANIM_ELAPSED_TIME = MOVE_ELAPSED_TIME + 0.4f;
        public static float FILP_LOTATION = 180;

        public static string GAME_SERVER_IP = "0.0.0.0";
        public static short GAME_SERVER_PORT = 7979;
        public static int MAX_CHAT_LINE = 2000;
        public static int INFO_UNIT = BUFFER_SIZE / sizeof(long);
        public static int PACKET_HOLD_COUNT = 10;
        public static int BROADCAST_CHUNK_SIZE = 120;
        public static int OUT_BOUND_LIMIT = 300;
        public static int BROADCAST_UNIT = BUFFER_SIZE / BROADCAST_CHUNK_SIZE;
        public static bool HEARTBEAT_ACTIVE = false;
        public static int TARGET_FRAME_RATE = 30;

        public static string REDIS_CONFIG = "127.0.0.1:6379,abortConnect=false";
    }
}
