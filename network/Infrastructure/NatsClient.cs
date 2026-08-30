using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NATS.Client;
using NATS.Client.JetStream;
using network.contracts.messaging;
using network.interfaces;

namespace network.infrastructure;

public class NatsClient : INatsClient
{
    private const string JetStreamMessageIdHeader = "Nats-Msg-Id";
    private readonly IConnection _connection;
    private readonly IJetStream _jetStream;
    private readonly IJetStreamManagement _jetStreamManagement;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _handlerCancellation = new();
    private readonly ConcurrentDictionary<long, Task> _inFlightHandlers = new();
    private readonly object _jetStreamManagementLock = new();
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
        var jetStreamOptions = JetStreamOptions.Builder()
            .WithRequestTimeout(5_000)
            .Build();
        _jetStream = _connection.CreateJetStreamContext(jetStreamOptions);
        _jetStreamManagement = _connection.CreateJetStreamManagementContext(jetStreamOptions);
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

    public void Subscribe(string subject, Action<string, byte[]> messageHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(messageHandler);

        void Handler(object? sender, MsgHandlerEventArgs args)
        {
            messageHandler(args.Message.Subject, args.Message.Data);
        }

        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var subscription = _connection.SubscribeAsync(subject, Handler);
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
        Func<string, byte[], CancellationToken, Task<byte[]>> messageHandler,
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

    public void EnsureDurableStream(NatsDurableStreamOptions options)
    {
        ValidateStreamOptions(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        lock (_jetStreamManagementLock)
        {
            StreamInfo? existing = null;
            try
            {
                existing = _jetStreamManagement.GetStreamInfo(options.Name);
            }
            catch (NATSJetStreamException ex) when (ex.ErrorCode == 404)
            {
                // Another server process may create the same stream concurrently.
            }

            if (existing == null)
            {
                try
                {
                    _jetStreamManagement.AddStream(BuildStreamConfiguration(options));
                    _logger?.LogInformation(
                        "JetStream stream created: Stream={Stream}, Subjects={Subjects}",
                        options.Name,
                        string.Join(',', options.Subjects));
                    return;
                }
                catch (NATSJetStreamException)
                {
                    existing = _jetStreamManagement.GetStreamInfo(options.Name);
                }
            }

            StreamConfiguration current = existing.Config;
            if (StreamMatches(current, options))
                return;

            StreamConfiguration updated = StreamConfiguration.Builder(current)
                .WithDescription(options.Description)
                .WithSubjects(options.Subjects.ToArray())
                .WithRetentionPolicy(RetentionPolicy.Limits)
                .WithStorageType(StorageType.File)
                .WithDiscardPolicy(DiscardPolicy.Old)
                .WithMaxAge(ToPositiveMilliseconds64(options.MaxAge, nameof(options.MaxAge)))
                .WithMaxBytes(options.MaxBytes)
                .WithDuplicateWindow(ToPositiveMilliseconds64(
                    options.DuplicateWindow,
                    nameof(options.DuplicateWindow)))
                // Treat the application value as a minimum. A managed or clustered NATS
                // deployment may already have stronger replication and must not be downgraded
                // when an application replica starts.
                .WithReplicas(Math.Max(current.Replicas, options.Replicas))
                .Build();
            _jetStreamManagement.UpdateStream(updated);
            _logger?.LogInformation("JetStream stream configuration updated: Stream={Stream}", options.Name);
        }
    }

    public async Task<NatsDurablePublishAck> PublishDurableAsync(
        string stream,
        string subject,
        string messageId,
        byte[] message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        var publishOptions = PublishOptions.Builder()
            .WithExpectedStream(stream)
            .WithMessageId(messageId)
            .Build();
        PublishAck ack = await _jetStream.PublishAsync(subject, message, publishOptions)
            .WaitAsync(cancellationToken);
        ack.ThrowOnHasError();
        return new NatsDurablePublishAck(ack.Stream, ack.Seq, ack.Duplicate);
    }

    public void SubscribeDurableQueue(
        NatsDurableConsumerOptions options,
        Func<NatsDurableMessage, CancellationToken, Task<NatsDurableMessageDisposition>> messageHandler)
    {
        ValidateConsumerOptions(options);
        ArgumentNullException.ThrowIfNull(messageHandler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        var consumerConfiguration = ConsumerConfiguration.Builder()
            .WithDurable(options.DurableName)
            .WithDeliverSubject(options.DeliverSubject)
            .WithDeliverGroup(options.QueueGroup)
            .WithFilterSubject(options.Subject)
            .WithDeliverPolicy(options.DeliverNewMessagesOnly ? DeliverPolicy.New : DeliverPolicy.All)
            .WithAckPolicy(AckPolicy.Explicit)
            .WithAckWait(ToPositiveMilliseconds64(options.AckWait, nameof(options.AckWait)))
            .WithMaxDeliver(options.MaxDeliver)
            .WithMaxAckPending(options.MaxAckPending)
            .Build();

        lock (_jetStreamManagementLock)
        {
            _jetStreamManagement.AddOrUpdateConsumer(options.StreamName, consumerConfiguration);
        }

        void Handler(object? sender, MsgHandlerEventArgs args)
        {
            if (!TryTrackHandler(
                    () => HandleDurableMessageAsync(args.Message, options, messageHandler),
                    $"durable message {args.Message.Subject}"))
            {
                TryNak(args.Message, options.RetryDelay, "shutdown");
            }
        }

        PushSubscribeOptions subscribeOptions = PushSubscribeOptions.BindTo(
            options.StreamName,
            options.DurableName);

        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            IJetStreamPushAsyncSubscription subscription = _jetStream.PushSubscribeAsync(
                options.Subject,
                options.QueueGroup,
                Handler,
                false,
                subscribeOptions);
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
        Func<string, byte[], CancellationToken, Task<byte[]>> messageHandler)
    {
        try
        {
            byte[] response = await messageHandler(
                message.Subject,
                message.Data,
                _handlerCancellation.Token);
            ArgumentNullException.ThrowIfNull(response);
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

    private async Task HandleDurableMessageAsync(
        Msg message,
        NatsDurableConsumerOptions options,
        Func<NatsDurableMessage, CancellationToken, Task<NatsDurableMessageDisposition>> messageHandler)
    {
        MetaData metadata = message.MetaData;
        string? messageId = message.HasHeaders ? message.Header[JetStreamMessageIdHeader] : null;
        var durableMessage = new NatsDurableMessage(
            message.Subject,
            message.Data,
            messageId,
            metadata.StreamSequence,
            metadata.NumDelivered);

        NatsDurableMessageDisposition disposition;
        try
        {
            disposition = await messageHandler(durableMessage, _handlerCancellation.Token);
        }
        catch (OperationCanceledException) when (_handlerCancellation.IsCancellationRequested)
        {
            disposition = NatsDurableMessageDisposition.Retry;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "JetStream durable handler failed; message will be retried: Stream={Stream}, Subject={Subject}, Sequence={Sequence}, Delivery={Delivery}",
                metadata.Stream,
                message.Subject,
                metadata.StreamSequence,
                metadata.NumDelivered);
            disposition = NatsDurableMessageDisposition.Retry;
        }

        try
        {
            switch (disposition)
            {
                case NatsDurableMessageDisposition.Ack:
                    message.AckSync(ToPositiveMilliseconds(
                        options.AckConfirmationTimeout,
                        nameof(options.AckConfirmationTimeout)));
                    break;
                case NatsDurableMessageDisposition.Retry:
                    message.NakWithDelay(ToPositiveMilliseconds64(
                        options.RetryDelay,
                        nameof(options.RetryDelay)));
                    break;
                case NatsDurableMessageDisposition.Terminate:
                    message.Term();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(disposition),
                        disposition,
                        "Unknown durable message disposition.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "JetStream acknowledgement failed; AckWait redelivery remains the fallback: Stream={Stream}, Subject={Subject}, Sequence={Sequence}, Disposition={Disposition}",
                metadata.Stream,
                message.Subject,
                metadata.StreamSequence,
                disposition);
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

    private void TryNak(Msg message, TimeSpan retryDelay, string reason)
    {
        try
        {
            message.NakWithDelay(ToPositiveMilliseconds64(retryDelay, nameof(retryDelay)));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(
                ex,
                "JetStream NAK failed; AckWait redelivery remains the fallback: Subject={Subject}, Reason={Reason}",
                message.Subject,
                reason);
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

    private static StreamConfiguration BuildStreamConfiguration(NatsDurableStreamOptions options)
    {
        return StreamConfiguration.Builder()
            .WithName(options.Name)
            .WithDescription(options.Description)
            .WithSubjects(options.Subjects.ToArray())
            .WithRetentionPolicy(RetentionPolicy.Limits)
            .WithStorageType(StorageType.File)
            .WithDiscardPolicy(DiscardPolicy.Old)
            .WithMaxAge(ToPositiveMilliseconds64(options.MaxAge, nameof(options.MaxAge)))
            .WithMaxBytes(options.MaxBytes)
            .WithDuplicateWindow(ToPositiveMilliseconds64(
                options.DuplicateWindow,
                nameof(options.DuplicateWindow)))
            .WithReplicas(options.Replicas)
            .Build();
    }

    private static bool StreamMatches(StreamConfiguration current, NatsDurableStreamOptions options)
    {
        return string.Equals(current.Description, options.Description, StringComparison.Ordinal) &&
               current.Subjects.ToHashSet(StringComparer.Ordinal)
                   .SetEquals(options.Subjects) &&
               current.RetentionPolicy == RetentionPolicy.Limits &&
               current.StorageType == StorageType.File &&
               current.DiscardPolicy == DiscardPolicy.Old &&
               current.MaxAge.Nanos ==
               checked(ToPositiveMilliseconds64(options.MaxAge, nameof(options.MaxAge)) * 1_000_000) &&
               current.MaxBytes == options.MaxBytes &&
               current.DuplicateWindow.Nanos ==
               checked(ToPositiveMilliseconds64(
                   options.DuplicateWindow,
                   nameof(options.DuplicateWindow)) * 1_000_000) &&
               current.Replicas >= options.Replicas;
    }

    private static void ValidateStreamOptions(NatsDurableStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentNullException.ThrowIfNull(options.Subjects);
        if (options.Subjects.Count == 0 || options.Subjects.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one valid stream subject is required.", nameof(options));
        _ = ToPositiveMilliseconds64(options.MaxAge, nameof(options.MaxAge));
        _ = ToPositiveMilliseconds64(options.DuplicateWindow, nameof(options.DuplicateWindow));
        if (options.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxBytes));
        if (options.Replicas <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.Replicas));
    }

    private static void ValidateConsumerOptions(NatsDurableConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StreamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DurableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.QueueGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeliverSubject);
        _ = ToPositiveMilliseconds64(options.AckWait, nameof(options.AckWait));
        _ = ToPositiveMilliseconds64(options.RetryDelay, nameof(options.RetryDelay));
        _ = ToPositiveMilliseconds(options.AckConfirmationTimeout, nameof(options.AckConfirmationTimeout));
        if (options.MaxDeliver <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxDeliver));
        if (options.MaxAckPending <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxAckPending));
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
