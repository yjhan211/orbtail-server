using Newtonsoft.Json;

namespace network
{
    public class Log
    {
        public string? stack_trace { get; private set; }
        public string? class_info { get; private set; }
        public string? message { get; private set; }
        DateTime timestamp { get; }

        public Log(Exception? exception = null, string? message = null)
        {
            this.stack_trace = exception?.StackTrace;
            this.message = message ?? "none";
            this.timestamp = DateTime.Now;
        }

        public void SetClassInfo<T>(T class_info)
        {
            this.class_info = JsonConvert.SerializeObject(class_info);
        }

        public string ParseString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
