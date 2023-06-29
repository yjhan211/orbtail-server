namespace network
{
    public class Config
    {
        public static readonly int MAX_CONNECTION = 1000;
        public static readonly int PRE_ALLOC_COUNT = 2;
        public static readonly int BUFFER_SIZE = 1024;
        public static readonly int HEADER_SIZE = 4;
        public static readonly string IP = "0.0.0.0";
        public static readonly int PORT = 7979;
        public static readonly int BACK_LOG = 100;
    }
}
