namespace network
{
    using NATS.Client;
    using System;
    using System.Text;
    using System.Threading.Tasks;

    public class NatsClient
    {
        private IConnection _connection;

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
}
