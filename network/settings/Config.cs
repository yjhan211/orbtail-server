namespace network
{
    public class Config
    {
        public static readonly int MAX_CONNECTION = 1000;
        public static readonly int PRE_ALLOC_COUNT = 2;
        public static readonly int BUFFER_SIZE = 1024;
        public static readonly int HEADER_SIZE = 4;

        public static readonly int BACK_LOG = 100;

        public static readonly float SPEED = 1.7f;
        public static readonly float MOVE_ELAPSED_TIME = 1f / SPEED;
        public static readonly float MOVE_ANIM_ELAPSED_TIME = MOVE_ELAPSED_TIME + 0.2f;
        public static float FILP_LOTATION = 180;

        public static string GAME_SERVER_IP = "127.0.0.1";
        public static short GAME_SERVER_PORT = 7979;
        public static int MAX_CHAT_LINE = 2000;
    }
}
