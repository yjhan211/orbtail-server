using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client;
namespace network.infrastructure.messaging;

/// <summary>
///     User Server와 Game Server가 공유하는 Core NATS 클라이언트.
///     하나의 NATS 연결로 publish/subscribe와 request/reply를 제공하고,
///     구독 목록과 연결 해제·재연결 상태를 관리한다.
///
///     종료 시 새 요청 처리를 막고 구독을 해제한 뒤, 진행 중인 비동기 request handler를 제한 시간 동안 기다린다.
///     제한을 넘으면 handler를 취소하고 다시 정리를 기다리되, 취소를 무시해도 연결 종료가 무한히 막히지는 않는다.
///
///     JetStream을 사용하지 않으므로 메시지를 저장하거나 재전달하지 않는다.
///     따라서 상태의 정본이 아닌 서버 간 알림과 세션 라우팅에 사용한다.
/// </summary>
public class NatsClient : INatsClient
{
    private static readonly TimeSpan DefaultHandlerShutdownGracePeriod = TimeSpan.FromSeconds(5);

    private readonly IConnection _connection;
    private readonly TimeSpan _handlerShutdownGracePeriod;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _handlerCancellation = new();
    private readonly ConcurrentDictionary<long, Task> _inFlightHandlers = new();
    private readonly object _subscriptionLock = new();
    private readonly List<IAsyncSubscription> _subscriptions = [];
    private readonly string _url;
    private int _closed;
    private long _nextHandlerId;

    public NatsClient(string url, ILogger? logger = null)
    {
        _url = url;
        _logger = logger ?? NullLogger.Instance;
        _handlerShutdownGracePeriod = DefaultHandlerShutdownGracePeriod;
        _connection = CreateConnection();
    }

    internal NatsClient(
        IConnection connection,
        TimeSpan handlerShutdownGracePeriod,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (handlerShutdownGracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handlerShutdownGracePeriod));

        _url = "injected";
        _logger = logger ?? NullLogger.Instance;
        _handlerShutdownGracePeriod = handlerShutdownGracePeriod;
        _connection = connection;
    }

    public void Publish(string subject, byte[] message)
    {
        try
        {
            _connection.Publish(subject, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NATS Publish 실패: Subject={Subject}", subject);
            throw;
        }
    }

    public void Subscribe(string subject, Action<string, byte[]> messageHandler, string? queue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(messageHandler);
        if (queue != null)
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        void Handler(object? sender, MsgHandlerEventArgs args)
        {
            try
            {
                messageHandler(args.Message.Subject, args.Message.Data);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS subscription handler failed: Subject={Subject}",
                    args.Message.Subject);
            }
        }

        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var subscription = queue == null
                ? _connection.SubscribeAsync(subject, Handler)
                : _connection.SubscribeAsync(subject, queue, Handler);
            _subscriptions.Add(subscription);
        }
    }

    public async Task<byte[]> RequestAsync(
        string subject,
        byte[] message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        int timeoutMilliseconds = ToPositiveMilliseconds(timeout, nameof(timeout));
        var response = await _connection.RequestAsync(
            subject,
            message,
            timeoutMilliseconds,
            cancellationToken);
        return response.Data;
    }

    public void SubscribeRequest(
        string subject,
        Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler,
        string? queue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(messageHandler);
        if (queue != null)
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        void Handler(object? sender, MsgHandlerEventArgs args)
        {
            if (!TryTrackHandler(
                    () => HandleRequestAsync(args.Message, messageHandler),
                    $"request {args.Message.Subject}"))
            {
                _logger.LogDebug(
                    "NATS request ignored during shutdown: Subject={Subject}",
                    args.Message.Subject);
            }
        }

        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var subscription = queue == null
                ? _connection.SubscribeAsync(subject, Handler)
                : _connection.SubscribeAsync(subject, queue, Handler);
            _subscriptions.Add(subscription);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginClose(out var subscriptions))
            return;

        UnsubscribeAll(subscriptions);
        try
        {
            if (!await WaitForInFlightHandlersAsync(cancellationToken))
            {
                _logger.LogWarning(
                    "NATS request handlers exceeded the graceful shutdown period; canceling: Count={Count}, GracePeriod={GracePeriod}",
                    _inFlightHandlers.Count,
                    _handlerShutdownGracePeriod);
                await _handlerCancellation.CancelAsync();

                if (!await WaitForInFlightHandlersAsync(cancellationToken))
                {
                    _logger.LogWarning(
                        "NATS request handlers did not stop after cancellation; closing connection: Count={Count}, GracePeriod={GracePeriod}",
                        _inFlightHandlers.Count,
                        _handlerShutdownGracePeriod);
                }
            }
        }
        finally
        {
            try
            {
                await _handlerCancellation.CancelAsync();
            }
            finally
            {
                _connection.Close();
            }
        }
    }

    public void Close()
    {
        if (!TryBeginClose(out var subscriptions))
            return;
        UnsubscribeAll(subscriptions);
        try
        {
            _handlerCancellation.Cancel();
        }
        finally
        {
            _connection.Close();
        }
    }

    private async Task HandleRequestAsync(
        Msg message,
        Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler)
    {
        try
        {
            byte[]? response = await messageHandler(
                message.Subject,
                message.Data,
                _handlerCancellation.Token);
            if (response == null)
                return;
            message.Respond(response);
        }
        catch (OperationCanceledException) when (_handlerCancellation.IsCancellationRequested)
        {
            _logger.LogDebug("NATS request canceled during shutdown: Subject={Subject}", message.Subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NATS request handler failed: Subject={Subject}", message.Subject);
        }
    }

    private bool TryTrackHandler(Func<Task> operation, string operationName)
    {
        lock (_subscriptionLock)
        {
            if (Volatile.Read(ref _closed) != 0)
                return false;

            long handlerId = Interlocked.Increment(ref _nextHandlerId);
            var task = Task.Run(async () =>
            {
                try
                {
                    await operation();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled NATS operation failure: Operation={Operation}", operationName);
                }
            });
            _inFlightHandlers.TryAdd(handlerId, task);
            _ = task.ContinueWith(
                completedTask => _inFlightHandlers.TryRemove(handlerId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
    }

    private async Task<bool> WaitForInFlightHandlersAsync(CancellationToken cancellationToken)
    {
        var inFlight = _inFlightHandlers.Values.ToArray();
        if (inFlight.Length == 0)
            return true;

        try
        {
            await Task.WhenAll(inFlight).WaitAsync(_handlerShutdownGracePeriod, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private bool TryBeginClose(out IAsyncSubscription[] subscriptions)
    {
        lock (_subscriptionLock)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                subscriptions = [];
                return false;
            }

            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
            return true;
        }
    }

    private void UnsubscribeAll(IEnumerable<IAsyncSubscription> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            try
            {
                subscription.Unsubscribe();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NATS 구독 해제 실패: Subject={Subject}", subscription.Subject);
            }
        }
    }

    private static int ToPositiveMilliseconds(TimeSpan value, string parameterName)
    {
        double milliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (milliseconds is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A positive duration no greater than Int32.MaxValue milliseconds is required.");
        }

        return (int)milliseconds;
    }

    private IConnection CreateConnection()
    {
        var options = ConnectionFactory.GetDefaultOptions();
        options.Url = _url;
        options.MaxReconnect = Options.ReconnectForever;
        options.ReconnectWait = 2000; // 2초 간격 재연결 시도

        options.DisconnectedEventHandler += (_, args) =>
        {
            _logger.LogWarning("NATS 연결 끊김: {Error}", args.Error?.Message ?? "unknown");
        };

        options.ReconnectedEventHandler += (_, _) =>
        {
            _logger.LogInformation("NATS 재연결 성공: {Url}", _url);
        };

        options.ClosedEventHandler += (_, _) => { _logger.LogWarning("NATS 연결 종료: {Url}", _url); };

        return new ConnectionFactory().CreateConnection(options);
    }
}

/// <summary>
///     생성 시 확정된 endpoint로 독립적인 NatsClient 연결을 만든다.
/// </summary>
public class NatsClientFactory
{
    private readonly ILogger<NatsClient>? _logger;
    private readonly string _natsEndpoint;

    public NatsClientFactory(string natsEndpoint, ILogger<NatsClient>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(natsEndpoint);
        _natsEndpoint = natsEndpoint;
        _logger = logger;
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
