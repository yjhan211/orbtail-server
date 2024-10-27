namespace network.common;

public class Config
{
    public static readonly int MAX_CONNECTION = 1000;
    public static readonly int PRE_ALLOC_COUNT = 2;
    public static readonly int BUFFER_SIZE = 2048;
    public static readonly int HEADER_SIZE = 4;
    public static readonly int BACK_LOG = 100;
    public static readonly int BATCH_SIZE = 10;
    public static readonly float SPEED = 1.5f;
    public static readonly float MOVE_ELAPSED_TIME = 0.5f / SPEED;
    public static readonly int BROADCAST_CHUNK_SIZE = 200;
    public static readonly int BROADCAST_UNIT = BUFFER_SIZE / BROADCAST_CHUNK_SIZE;
    public static readonly bool HEARTBEAT_ACTIVE = true;
    public static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(30);
    public static readonly int MAX_CHAT_LENGTH = 100;
    public static readonly Cell START_POSITION = new(88, 132);

    public static readonly List<(int, int)> DEFAULT_ITEM_LIST =
        [(101000001, 1), (103000001, 1), (102000001, 1), (102000002, 1)];

    public static readonly int MAX_QUEUE_SIZE = 10;
}