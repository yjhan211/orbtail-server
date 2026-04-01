using Microsoft.Extensions.Logging;
using NATS.Client;
using network.interfaces;

namespace network.infrastructure;

public class NatsClient : INatsClient
{
    private readonly string _url;
    private readonly ILogger? _logger;
    private readonly List<IAsyncSubscription> _subscriptions = [];
    private readonly List<(string Subject, Action<string, byte[]> Handler)> _subscribedTopics = [];
    private IConnection _connection;

    public NatsClient(string url, ILogger? logger = null)
    {
        _url = url;
        _logger = logger;
        _connection = CreateConnection();
    }

    private IConnection CreateConnection()
    {
        var options = ConnectionFactory.GetDefaultOptions();
        options.Url = _url;
        options.MaxReconnect = Options.ReconnectForever;
        options.ReconnectWait = 2000; // 2초 간격 재연결 시도

        options.DisconnectedEventHandler += (_, args) =>
        {
            _logger?.LogWarning("NATS 연결 끊김: {Error}", args.Error?.Message ?? "unknown");
        };

        options.ReconnectedEventHandler += (_, _) =>
        {
            _logger?.LogInformation("NATS 재연결 성공: {Url}", _url);
            ResubscribeAll();
        };

        options.ClosedEventHandler += (_, _) =>
        {
            _logger?.LogWarning("NATS 연결 종료: {Url}", _url);
        };

        return new ConnectionFactory().CreateConnection(options);
    }

    /// <summary>
    /// 재연결 후 기존 구독 복원
    /// </summary>
    private void ResubscribeAll()
    {
        _subscriptions.Clear();
        foreach (var (subject, handler) in _subscribedTopics)
        {
            var localHandler = handler;
            void NatsHandler(object? sender, MsgHandlerEventArgs args)
            {
                localHandler(args.Message.Subject, args.Message.Data);
            }

            var subscription = _connection.SubscribeAsync(subject, NatsHandler);
            _subscriptions.Add(subscription);
        }

        _logger?.LogInformation("NATS 구독 복원 완료: {Count}개", _subscribedTopics.Count);
    }

    public void Publish(string subject, byte[] message)
    {
        _connection.Publish(subject, message);
    }

    public void Subscribe(string subject, Action<string, byte[]> messageHandler)
    {
        // 구독 정보 저장 (재연결 시 복원용)
        _subscribedTopics.Add((subject, messageHandler));

        void Handler(object? sender, MsgHandlerEventArgs args)
        {
            messageHandler(args.Message.Subject, args.Message.Data);
        }

        var subscription = _connection.SubscribeAsync(subject, Handler);
        _subscriptions.Add(subscription);
    }

    public void Close()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Unsubscribe();
        }

        _subscriptions.Clear();
        _subscribedTopics.Clear();
        _connection.Close();
    }
}

/// <summary>
/// NatsClient 팩토리
/// </summary>
public class NatsClientFactory : INatsClientFactory
{
    private string _natsEndpoint = "";
    private readonly ILogger<NatsClient>? _logger;

    public NatsClientFactory(ILogger<NatsClient>? logger = null)
    {
        _logger = logger;
    }

    public void Initialize(string natsEndPoint)
    {
        _natsEndpoint = natsEndPoint;
    }

    public INatsClient Create()
    {
        try
        {
            return new NatsClient(_natsEndpoint, _logger);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to create NatsClient", ex);
        }
    }
}
