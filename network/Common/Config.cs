namespace network.common;

public static class Config
{
    private static readonly int BroadcastChunkSize = 200;

    public static readonly int MAX_CONNECTION = 1000;
    public static readonly int PRE_ALLOC_COUNT = 2;
    public static readonly int BUFFER_SIZE = 2048;
    public static readonly int HEADER_SIZE = 4;
    public static readonly int BACK_LOG = 100;
    public static readonly int BATCH_SIZE = 10;
    public static readonly int BROADCAST_UNIT = BUFFER_SIZE / BroadcastChunkSize;
    public static readonly int MAX_MOVE_QUEUE_SIZE = 10;
    public static readonly int MAX_CHAT_LENGTH = 30;
    public static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(30);
}