using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.infrastructure.messaging;
using network.packets;

namespace user_server.sessions;

/// <summary>
///     라우터가 세션에 요구하는 것. <see cref="PlayerSession" />이 구현하고, 테스트는 가짜로 대신한다.
/// </summary>
internal interface IMatchingSessionEndpoint
{
    public bool TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet);
    public bool TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet);
    public bool TryDeliverAdmissionFailed(long matchingId, Packet packet);
    public void ClearMatchingAssignment(long matchingId);
    public void DisconnectIfOlderSession(long newGeneration);
}

/// <summary>
///     "이 플레이어의 세션이 어느 User Server에 있든" 패킷·배정 변경을 전달하는 port.
///     매칭 pass(리더)와 lifecycle 처리(큐 그룹 1곳)가 세션 위치를 몰라도 되게 한다.
/// </summary>
internal interface IPlayerSessionRouter
{
    public Task<bool> DeliverMatchingSuccessAsync(long playerId, long matchingId, string requestId, Packet packet);
    public Task<bool> DeliverMatchingFailedAsync(long playerId, long matchingId, string requestId, Packet packet);
    public Task<bool> DeliverAdmissionFailedAsync(long playerId, long matchingId, Packet packet);

    /// <summary>모든 프로세스에 배정 해제를 알린다(로컬은 즉시).</summary>
    public void ClearMatchingAssignment(long playerId, long matchingId);

    /// <summary>이 프로세스에 로그인이 들어왔음을 알려 다른 프로세스의 같은 플레이어 세션을 끊게 한다.</summary>
    public void AnnounceLogin(long playerId, long generation);
}

/// <summary>
///     로컬 세션이면 직접, 아니면 NATS request로 세션을 가진 프로세스에 위임하는 라우터.
///     Redis lease가 전역 현재 세대를 정본으로 보관하고, 이 라우터는 그 위치를 별도로 복제하지 않는다.
///     전달 요청은 모든 User Server가 구독하되 로컬 현재 세션을 가진 프로세스만 답하며,
///     원격 응답이 timeout되면 같은 요청을 한 번 재시도한다. 세션 보유 프로세스는 request ID별 첫 처리 결과를
///     잠시 보관해 응답만 유실된 재시도가 패킷을 중복 전송하지 않게 한다.
///     로그인 알림은 더 높은 세대의 로그인일 때만 옛 세션을 끊는다.
///     알림이 유실돼도 옛 세션은 다음 lease 갱신 실패 때 종료된다.
/// </summary>
internal sealed class NatsPlayerSessionRouter(
    INatsClient natsClient,
    Func<long, IMatchingSessionEndpoint?> getLocalSession,
    string nodeId,
    ILogger logger) : IPlayerSessionRouter
{
    public const string DeliverSubjectPrefix = "user_server.session.deliver.";
    public const string ClearSubject = "user_server.session.clear";
    public const string LoginSubject = "user_server.session.login";

    public const int RemoteDeliveryAttempts = 2;

    /// <summary>원격 전달 한 번의 응답 대기 시간. 두 번 모두 timeout되어도 기존 총 상한 1.5초를 유지한다.</summary>
    public static readonly TimeSpan RemoteAttemptTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>응답 유실 뒤 같은 요청이 돌아왔을 때 첫 처리 결과를 재사용하는 기간.</summary>
    public static readonly TimeSpan DeliveryResultLifetime = TimeSpan.FromMinutes(2);

    private static readonly byte[] Delivered = [1];
    private static readonly byte[] Rejected = [0];

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly ConcurrentDictionary<SessionDeliveryKey, CachedDeliveryResult> _deliveryResults = new();
    private long _handledRemoteDeliveryRequests;

    public string NodeId { get; } = nodeId;

    public void Start()
    {
        natsClient.SubscribeRequest(DeliverSubjectPrefix + "*", HandleDeliverRequestAsync);
        natsClient.Subscribe(ClearSubject, (_, body) => HandleClear(body));
        natsClient.Subscribe(LoginSubject, (_, body) => HandleLogin(body));
    }

    public Task<bool> DeliverMatchingSuccessAsync(long playerId, long matchingId, string requestId, Packet packet) =>
        DeliverAsync(SessionDeliveryOp.MatchingSuccess, playerId, matchingId, requestId, packet);

    public Task<bool> DeliverMatchingFailedAsync(long playerId, long matchingId, string requestId, Packet packet) =>
        DeliverAsync(SessionDeliveryOp.MatchingFailed, playerId, matchingId, requestId, packet);

    public Task<bool> DeliverAdmissionFailedAsync(long playerId, long matchingId, Packet packet) =>
        DeliverAsync(SessionDeliveryOp.AdmissionFailed, playerId, matchingId, string.Empty, packet);

    public void ClearMatchingAssignment(long playerId, long matchingId)
    {
        getLocalSession(playerId)?.ClearMatchingAssignment(matchingId);
        PublishBestEffort(ClearSubject, new SessionNotice
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            OriginNodeId = NodeId
        });
    }

    public void AnnounceLogin(long playerId, long generation)
    {
        PublishBestEffort(LoginSubject, new SessionNotice
        {
            PlayerId = playerId,
            OriginNodeId = NodeId,
            SessionGeneration = generation
        });
    }

    private async Task<bool> DeliverAsync(
        SessionDeliveryOp op,
        long playerId,
        long matchingId,
        string requestId,
        Packet packet)
    {
        IMatchingSessionEndpoint? local = getLocalSession(playerId);
        if (local != null)
            return Apply(local, op, matchingId, requestId, packet);

        // 길이 헤더는 송신 직전에야 기록되므로 wire로 옮기기 전에 여기서 채운다.
        packet.RecordSize();
        var request = new SessionDeliveryRequest
        {
            Op = op,
            PlayerId = playerId,
            MatchingId = matchingId,
            RequestId = requestId,
            PacketWire = packet.ToBytes(),
            OriginNodeId = NodeId
        };
        byte[] requestBody = MessagePackSerializer.Serialize(request, SerializerOptions);
        for (int attempt = 1; attempt <= RemoteDeliveryAttempts; attempt++)
        {
            try
            {
                byte[] reply = await natsClient.RequestAsync(
                    DeliverSubjectPrefix + playerId,
                    requestBody,
                    RemoteAttemptTimeout);
                return reply.Length == 1 && reply[0] == Delivered[0];
            }
            catch (Exception ex) when (IsTimeout(ex) && attempt < RemoteDeliveryAttempts)
            {
                logger.LogInformation(
                    "Remote session delivery response timed out; retrying same request: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}, Attempt={Attempt}",
                    op, playerId, matchingId, attempt);
            }
            catch (Exception ex)
            {
                // 최종 timeout은 세션 부재와 요청·응답 유실을 구분할 수 없다. 확인되지 않은 전달은 실패로 되돌린다.
                if (IsTimeout(ex))
                    logger.LogInformation(
                        "No user server confirmed the session delivery; delivery dropped: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}, Attempts={Attempts}",
                        op, playerId, matchingId, RemoteDeliveryAttempts);
                else
                    logger.LogWarning(ex,
                        "Remote session delivery failed: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                        op, playerId, matchingId);
                return false;
            }
        }

        return false;
    }

    private Task<byte[]?> HandleDeliverRequestAsync(string subject, byte[] body, CancellationToken cancellationToken)
    {
        SessionDeliveryRequest request;
        try
        {
            request = MessagePackSerializer.Deserialize<SessionDeliveryRequest>(body, SerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            logger.LogWarning(ex, "Malformed session delivery request: Subject={Subject}", subject);
            return Task.FromResult<byte[]?>(null);
        }

        var key = new SessionDeliveryKey(
            request.OriginNodeId,
            request.Op,
            request.PlayerId,
            request.MatchingId,
            request.RequestId);
        long now = Environment.TickCount64;
        if (TryGetCachedDeliveryResult(key, now, out bool cachedResult))
        {
            logger.LogInformation(
                "Remote session delivery result replayed without applying again: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}, From={Origin}, Delivered={Delivered}",
                request.Op, request.PlayerId, request.MatchingId, request.OriginNodeId, cachedResult);
            return Task.FromResult<byte[]?>(cachedResult ? Delivered : Rejected);
        }

        IMatchingSessionEndpoint? local = getLocalSession(request.PlayerId);
        if (local == null)
            return Task.FromResult<byte[]?>(null);

        var candidate = new CachedDeliveryResult(
            new Lazy<bool>(
                () => ApplyRemote(local, request),
                LazyThreadSafetyMode.ExecutionAndPublication),
            now + (long)DeliveryResultLifetime.TotalMilliseconds);
        CachedDeliveryResult stored = _deliveryResults.GetOrAdd(key, candidate);
        bool delivered = stored.Result.Value;
        PruneExpiredDeliveryResults(now);
        logger.LogInformation(
            "Remote session delivery handled: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}, From={Origin}, Delivered={Delivered}, Replayed={Replayed}",
            request.Op, request.PlayerId, request.MatchingId, request.OriginNodeId, delivered, !ReferenceEquals(stored, candidate));
        return Task.FromResult<byte[]?>(delivered ? Delivered : Rejected);
    }

    private static bool IsTimeout(Exception exception) =>
        exception is TimeoutException or NATS.Client.NATSTimeoutException;

    private static bool ApplyRemote(IMatchingSessionEndpoint session, SessionDeliveryRequest request)
    {
        using Packet packet = Packet.CreateForSending(request.PacketWire);
        return Apply(session, request.Op, request.MatchingId, request.RequestId, packet);
    }

    private bool TryGetCachedDeliveryResult(SessionDeliveryKey key, long now, out bool result)
    {
        if (_deliveryResults.TryGetValue(key, out CachedDeliveryResult? cached))
        {
            if (cached.ExpiresAtTick > now)
            {
                result = cached.Result.Value;
                return true;
            }

            ((ICollection<KeyValuePair<SessionDeliveryKey, CachedDeliveryResult>>)_deliveryResults)
                .Remove(new KeyValuePair<SessionDeliveryKey, CachedDeliveryResult>(key, cached));
        }

        result = false;
        return false;
    }

    private void PruneExpiredDeliveryResults(long now)
    {
        if ((Interlocked.Increment(ref _handledRemoteDeliveryRequests) & 63) != 0)
            return;

        foreach ((SessionDeliveryKey key, CachedDeliveryResult cached) in _deliveryResults)
        {
            if (cached.ExpiresAtTick > now)
                continue;
            ((ICollection<KeyValuePair<SessionDeliveryKey, CachedDeliveryResult>>)_deliveryResults)
                .Remove(new KeyValuePair<SessionDeliveryKey, CachedDeliveryResult>(key, cached));
        }
    }

    private static bool Apply(
        IMatchingSessionEndpoint session,
        SessionDeliveryOp op,
        long matchingId,
        string requestId,
        Packet packet)
    {
        return op switch
        {
            SessionDeliveryOp.MatchingSuccess => session.TryDeliverMatchingSuccess(matchingId, requestId, packet),
            SessionDeliveryOp.MatchingFailed => session.TryDeliverMatchingFailed(matchingId, requestId, packet),
            SessionDeliveryOp.AdmissionFailed => session.TryDeliverAdmissionFailed(matchingId, packet),
            _ => false
        };
    }

    private void HandleClear(byte[] body)
    {
        SessionNotice? notice = TryReadNotice(body, ClearSubject);
        if (notice == null || notice.OriginNodeId == NodeId)
            return;
        getLocalSession(notice.PlayerId)?.ClearMatchingAssignment(notice.MatchingId);
    }

    private void HandleLogin(byte[] body)
    {
        SessionNotice? notice = TryReadNotice(body, LoginSubject);
        if (notice == null || notice.OriginNodeId == NodeId)
            return;

        IMatchingSessionEndpoint? local = getLocalSession(notice.PlayerId);
        if (local == null)
            return;

        local.DisconnectIfOlderSession(notice.SessionGeneration);
    }

    private SessionNotice? TryReadNotice(byte[] body, string subject)
    {
        try
        {
            return MessagePackSerializer.Deserialize<SessionNotice>(body, SerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            logger.LogWarning(ex, "Malformed session notice: Subject={Subject}", subject);
            return null;
        }
    }

    private void PublishBestEffort(string subject, SessionNotice notice)
    {
        try
        {
            natsClient.Publish(subject, MessagePackSerializer.Serialize(notice, SerializerOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session notice publish failed: Subject={Subject}, PlayerId={PlayerId}", subject, notice.PlayerId);
        }
    }

    private readonly record struct SessionDeliveryKey(
        string OriginNodeId,
        SessionDeliveryOp Op,
        long PlayerId,
        long MatchingId,
        string RequestId);

    private sealed record CachedDeliveryResult(Lazy<bool> Result, long ExpiresAtTick);
}

internal enum SessionDeliveryOp : byte
{
    MatchingSuccess = 1,
    MatchingFailed = 2,
    AdmissionFailed = 3
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class SessionDeliveryRequest
{
    [Key(0)] public SessionDeliveryOp Op { get; set; }
    [Key(1)] public long PlayerId { get; set; }
    [Key(2)] public long MatchingId { get; set; }
    [Key(3)] public string RequestId { get; set; } = string.Empty;
    [Key(4)] public byte[] PacketWire { get; set; } = [];
    [Key(5)] public string OriginNodeId { get; set; } = string.Empty;
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class SessionNotice
{
    [Key(0)] public long PlayerId { get; set; }
    [Key(1)] public long MatchingId { get; set; }
    [Key(2)] public string OriginNodeId { get; set; } = string.Empty;
    [Key(3)] public long SessionGeneration { get; set; }
}
