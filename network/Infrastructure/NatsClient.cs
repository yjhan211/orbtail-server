using NATS.Client;

namespace network.infrastructure
{
    public class NatsClient
    {
        private readonly IConnection _connection;

        public NatsClient(string url)
        {
            var options = ConnectionFactory.GetDefaultOptions();
            options.Url = url;

            _connection = new ConnectionFactory().CreateConnection(options);
        }

        public void Publish(string subject, byte[] message)
        {
            _connection.Publish(subject, message);
        }

        public IAsyncSubscription Subscribe(string subject, Action<string, byte[]> messageHandler)
        {
            EventHandler<MsgHandlerEventArgs> handler = (sender, args) =>
            {
                messageHandler(args.Message.Subject, args.Message.Data);
            };

            return _connection.SubscribeAsync(subject, handler);
        }

        public void Close()
        {
            _connection?.Close();
        }
    }

    public class NatsClientFactory
    {
        private string _natsEndpoint = "";

        public void Initialize(string NatsEndPoint)
        {
            _natsEndpoint = NatsEndPoint;
        }

        public NatsClient Create()
        {
            try
            {
                return new NatsClient(_natsEndpoint);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to create NatsClient", ex);
            }
        }
    }
}
