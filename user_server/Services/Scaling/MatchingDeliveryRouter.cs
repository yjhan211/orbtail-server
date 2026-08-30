using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.interfaces;

namespace user_server.services.scaling;

public sealed class MatchingDeliveryRouter : IMatchingDeliveryRouter
{
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly UserServerClusterOptions _options;
    private readonly INatsClient _natsClient;
    private readonly ILogger<MatchingDeliveryRouter> _logger;
    private readonly ConcurrentDictionary<long, Task> _outboundRequests = new();
    private readonly object _stateLock = new();
    private Func<MatchingDeliveryRequest, CancellationToken, Task<MatchingDeliveryResponse>>? _handler;
    private Task? _stopTask;
    private long _nextRequestId;
    private int _started;
    private int _stopping;

    public MatchingDeliveryRouter(
        UserServerProcessIdentity identity,
        UserServerClusterOptions options,
        INatsClient natsClient,
        ILogger<MatchingDeliveryRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(natsClient);
        ArgumentNullException.ThrowIfNull(logger);
        if (!identity.IsValid)
            throw new ArgumentException("UserServer process identity is invalid.", nameof(identity));

        options.Validate();
        Identity = identity;
        Subject = MatchingDeliverySubjects.For(identity);
        _options = options;
        _natsClient = natsClient;
        _logger = logger;
    }

    public UserServerProcessIdentity Identity { get; }
    public string Subject { get; }

    public void Start(Func<MatchingDeliveryRequest, CancellationToken, Task<MatchingDeliveryResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
            if (Volatile.Read(ref _started) != 0)
                throw new InvalidOperationException("Matching delivery router is already started.");

            _handler = handler;
            try
            {
                // The subject already identifies one exact process generation. A queue group would
                // allow an unrelated process to consume the request, so it must remain null.
                _natsClient.SubscribeRequest(Subject, HandleRequestAsync, queue: null);
                Volatile.Write(ref _started, 1);
            }
            catch
            {
                _handler = null;
                throw;
            }
        }
    }

    public Task<MatchingDeliveryResponse> DeliverAsync(
        MatchingDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.HasValidRoute)
            throw new ArgumentException("Matching delivery request has an invalid route.", nameof(request));

        long requestId;
        Task<MatchingDeliveryResponse> task;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _started) == 0)
                throw new InvalidOperationException("Matching delivery router has not been started.");
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);

            requestId = Interlocked.Increment(ref _nextRequestId);
            task = DeliverCoreAsync(request, cancellationToken);
            if (!_outboundRequests.TryAdd(requestId, task))
                throw new InvalidOperationException("Could not track the matching delivery request.");
        }

        _ = task.ContinueWith(
            completedTask => _outboundRequests.TryRemove(requestId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_stateLock)
        {
            if (_stopTask == null)
            {
                Volatile.Write(ref _stopping, 1);
                _stopTask = StopCoreAsync();
            }

            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }

    private async Task<MatchingDeliveryResponse> DeliverCoreAsync(
        MatchingDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        byte[] payload = MessagePackSerializer.Serialize(request, SerializerOptions, cancellationToken);
        string targetSubject = MatchingDeliverySubjects.For(
            request.OwnerNodeId,
            request.OwnerNodeGeneration);

        Exception? lastError = null;
        for (int attempt = 1; attempt <= _options.DeliveryMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] responsePayload = await _natsClient.RequestAsync(
                    targetSubject,
                    payload,
                    _options.DeliveryRequestTimeout,
                    cancellationToken);
                MatchingDeliveryResponse? response = MessagePackSerializer.Deserialize<MatchingDeliveryResponse>(
                    responsePayload,
                    SerializerOptions,
                    cancellationToken);
                if (response == null ||
                    !Enum.IsDefined(response.Status) ||
                    !string.Equals(response.DeliveryId, request.DeliveryId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Matching delivery response was invalid or did not match the request delivery id.");
                }

                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= _options.DeliveryMaxAttempts)
                    break;

                _logger.LogWarning(
                    ex,
                    "Matching delivery request failed; retrying with the same delivery id: DeliveryId={DeliveryId}, Attempt={Attempt}, MaxAttempts={MaxAttempts}",
                    request.DeliveryId,
                    attempt,
                    _options.DeliveryMaxAttempts);
                if (_options.DeliveryRetryDelay > TimeSpan.Zero)
                    await Task.Delay(_options.DeliveryRetryDelay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Matching delivery request failed after {_options.DeliveryMaxAttempts} attempts.",
            lastError);
    }

    private async Task<byte[]> HandleRequestAsync(
        string _,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        MatchingDeliveryRequest? request = null;
        try
        {
            request = MessagePackSerializer.Deserialize<MatchingDeliveryRequest>(
                payload,
                SerializerOptions,
                cancellationToken);
            if (request == null || !request.HasValidRoute)
                return SerializeResponse(request?.DeliveryId, MatchingDeliveryStatus.InvalidRequest);
            if (!string.Equals(request.OwnerNodeId, Identity.NodeId, StringComparison.Ordinal) ||
                !string.Equals(request.OwnerNodeGeneration, Identity.Generation, StringComparison.Ordinal))
            {
                return SerializeResponse(request.DeliveryId, MatchingDeliveryStatus.StaleOwner);
            }

            if (Volatile.Read(ref _stopping) != 0)
                return SerializeResponse(request.DeliveryId, MatchingDeliveryStatus.ShuttingDown);

            var handler = _handler;
            if (handler == null)
                return SerializeResponse(request.DeliveryId, MatchingDeliveryStatus.ShuttingDown);

            MatchingDeliveryResponse response = await handler(request, cancellationToken);
            if (response == null ||
                !string.Equals(response.DeliveryId, request.DeliveryId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Matching delivery handler returned an invalid response: DeliveryId={DeliveryId}",
                    request.DeliveryId);
                return SerializeResponse(request.DeliveryId, MatchingDeliveryStatus.RetryableFailure);
            }

            return MessagePackSerializer.Serialize(response, SerializerOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SerializeResponse(request?.DeliveryId, MatchingDeliveryStatus.ShuttingDown);
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogWarning(ex, "Rejected malformed matching delivery request");
            return SerializeResponse(request?.DeliveryId, MatchingDeliveryStatus.InvalidRequest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Matching delivery handler failed: DeliveryId={DeliveryId}",
                request?.DeliveryId ?? string.Empty);
            return SerializeResponse(request?.DeliveryId, MatchingDeliveryStatus.RetryableFailure);
        }
    }

    private static byte[] SerializeResponse(string? deliveryId, MatchingDeliveryStatus status)
    {
        return MessagePackSerializer.Serialize(
            MatchingDeliveryResponse.Create(deliveryId, status),
            SerializerOptions);
    }

    private async Task StopCoreAsync()
    {
        try
        {
            while (!_outboundRequests.IsEmpty)
            {
                Task[] requests = _outboundRequests.Values.ToArray();
                if (requests.Length == 0)
                    break;

                try
                {
                    await Task.WhenAll(requests);
                }
                catch (Exception ex)
                {
                    // Individual callers observe their own request failure. Shutdown still needs to
                    // close the dedicated NATS connection and drain inbound handlers.
                    _logger.LogDebug(ex, "Matching delivery request failed while the router was draining");
                }
            }
        }
        finally
        {
            await _natsClient.CloseAsync(CancellationToken.None);
            _handler = null;
        }
    }
}
