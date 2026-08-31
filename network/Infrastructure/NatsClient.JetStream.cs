using Microsoft.Extensions.Logging;
using NATS.Client;
using NATS.Client.JetStream;
using network.contracts.messaging;

namespace network.infrastructure;

/// <summary>
///     Contains durable JetStream stream, consumer, publish, and acknowledgement behavior for
///     <see cref="NatsClient"/>. Core request/reply and connection lifecycle remain in the primary partial.
/// </summary>
public partial class NatsClient
{
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

}
