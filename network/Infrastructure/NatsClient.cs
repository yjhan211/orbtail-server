using NATS.Client;

namespace network.infrastructure;

public class NatsClient
{
    private readonly IConnection _connection;
    private readonly List<IAsyncSubscription> _subscriptions;

    public NatsClient(string url)
    {
        var options = ConnectionFactory.GetDefaultOptions();
        options.Url = url;

        _connection = new ConnectionFactory().CreateConnection(options);
        _subscriptions = [];
    }

    public void Publish(string subject, byte[] message)
    {
        _connection.Publish(subject, message);
    }

    public void Subscribe(string subject, Action<string, byte[]> messageHandler)
    {
        void Handler(object? sender, MsgHandlerEventArgs args)
        {
            messageHandler(args.Message.Subject, args.Message.Data);
        }

        var subscription = _connection.SubscribeAsync(subject, Handler);
        _subscriptions.Add(subscription);
    }

    public void Close()
    {
        foreach (var subscription in _subscriptions) subscription.Unsubscribe();

        _subscriptions.Clear();
        _connection.Close();
    }
}

public class NatsClientFactory
{
    private string _natsEndpoint = "";

    public void Initialize(string natsEndPoint)
    {
        _natsEndpoint = natsEndPoint;
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