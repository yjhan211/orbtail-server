using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NATS.Client;
using network.interfaces;

namespace network.infrastructure;

/// <summary>
///     Owns one NATS connection and coordinates core publish, subscribe, request/reply, and graceful handler drain.
/// </summary>
public class NatsClient : INatsClient
{
    private readonly IConnection _connection;
    private readonly ILogger? _logger;
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
        _logger = logger;
        _connection = CreateConnection();
    }

    public void Publish(string subject, byte[] message)
    {
        try
        {
            _connection.Publish(subject, message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "NATS Publish 실패: Subject={Subject}", subject);
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
            messageHandler(args.Message.Subject, args.Message.Data);
        }

        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            IAsyncSubscription subscription = queue == null
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
        Msg response = await _connection.RequestAsync(
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
                _logger?.LogDebug(
                    "NATS request ignored during shutdown: Subject={Subject}",
                    args.Message.Subject);
            }
        }

        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            IAsyncSubscription subscription = queue == null
                ? _connection.SubscribeAsync(subject, Handler)
                : _connection.SubscribeAsync(subject, queue, Handler);
            _subscriptions.Add(subscription);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginClose(out IAsyncSubscription[] subscriptions))
            return;

        UnsubscribeAll(subscriptions);
        try
        {
            while (!_inFlightHandlers.IsEmpty)
            {
                Task[] inFlight = _inFlightHandlers.Values.ToArray();
                if (inFlight.Length == 0)
                    break;
                await Task.WhenAll(inFlight).WaitAsync(cancellationToken);
            }
        }
        finally
        {
            _handlerCancellation.Cancel();
            _connection.Close();
        }
    }

    public void Close()
    {
        if (!TryBeginClose(out IAsyncSubscription[] subscriptions))
            return;
        UnsubscribeAll(subscriptions);
        _handlerCancellation.Cancel();
        _connection.Close();
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
            // null은 "내 담당이 아니다" — 다른 구독자가 답하도록 침묵한다. 요청자는 타임아웃으로 부재를 안다.
            if (response == null)
                return;
            message.Respond(response);
        }
        catch (OperationCanceledException) when (_handlerCancellation.IsCancellationRequested)
        {
            _logger?.LogDebug("NATS request canceled during shutdown: Subject={Subject}", message.Subject);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "NATS request handler failed: Subject={Subject}", message.Subject);
        }
    }

    private bool TryTrackHandler(Func<Task> operation, string operationName)
    {
        lock (_subscriptionLock)
        {
            if (Volatile.Read(ref _closed) != 0)
                return false;

            long handlerId = Interlocked.Increment(ref _nextHandlerId);
            Task task = Task.Run(async () =>
            {
                try
                {
                    await operation();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unhandled NATS operation failure: Operation={Operation}", operationName);
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
        foreach (IAsyncSubscription subscription in subscriptions)
        {
            try
            {
                subscription.Unsubscribe();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "NATS 구독 해제 실패: Subject={Subject}", subscription.Subject);
            }
        }
    }

    private static int ToPositiveMilliseconds(TimeSpan value, string parameterName)
    {
        long milliseconds = ToPositiveMilliseconds64(value, parameterName);
        if (milliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "The duration is too long.");
        return (int)milliseconds;
    }

    private static long ToPositiveMilliseconds64(TimeSpan value, string parameterName)
    {
        double totalMilliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (!double.IsFinite(totalMilliseconds) || totalMilliseconds <= 0 || totalMilliseconds > long.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "A positive finite duration is required.");
        return (long)totalMilliseconds;
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
        };

        options.ClosedEventHandler += (_, _) => { _logger?.LogWarning("NATS 연결 종료: {Url}", _url); };

        return new ConnectionFactory().CreateConnection(options);
    }
}

/// <summary>
///     NatsClient 팩토리
/// </summary>
public class NatsClientFactory(ILogger<NatsClient>? logger = null) : INatsClientFactory
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
            return new NatsClient(_natsEndpoint, logger);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to create NatsClient", ex);
        }
    }
}
