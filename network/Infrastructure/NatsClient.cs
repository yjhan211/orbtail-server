using NATS.Client;
using network.interfaces;

namespace network.infrastructure;

public class NatsClient : INatsClient
{
    private readonly NATS.Client.IConnection _connection;
    private readonly List<NATS.Client.IAsyncSubscription> _subscriptions;

    public NatsClient(string url)
    {
        var options = NATS.Client.ConnectionFactory.GetDefaultOptions();
        options.Url = url;

        _connection = new NATS.Client.ConnectionFactory().CreateConnection(options);
        _subscriptions = [];
    }

    public void Publish(string subject, byte[] message)
    {
        _connection.Publish(subject, message);
    }

    public void Subscribe(string subject, Action<string, byte[]> messageHandler)
    {
        void Handler(object? sender, NATS.Client.MsgHandlerEventArgs args)
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

// 기존 NatsClientFactory 클래스 리팩터링
public class NatsClientFactory : INatsClientFactory
{
    private string _natsEndpoint = "";

    public void Initialize(string natsEndPoint)
    {
        _natsEndpoint = natsEndPoint;
    }

    public INatsClient Create()
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