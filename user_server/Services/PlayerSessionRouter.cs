using MessagePack;
using Microsoft.Extensions.Logging;
using network.interfaces;
using network.packets;

namespace user_server.services;

/// <summary>
///     라우터가 세션에 요구하는 것. <see cref="network.GameSession" />이 구현하고, 테스트는 가짜로 대신한다.
/// </summary>
internal interface IMatchingSessionEndpoint
{
    public bool TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet);
    public bool TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet);
    public bool TryDeliverAdmissionFailed(long matchingId, Packet packet);
    public void ClearMatchingAssignment(long matchingId);
    public void DisconnectForDuplicateLogin();
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
    public void AnnounceLogin(long playerId);
}

/// <summary>
///     로컬 세션이면 직접, 아니면 NATS request로 세션을 가진 프로세스에 위임하는 라우터.
///     전달 요청은 모든 User Server가 구독하되 세션을 가진 프로세스만 답한다(나머지는 침묵) — 세션 소유자 레지스트리 없이
///     "첫 응답 = 소유자"로 푼다. 아무도 답하지 않으면 타임아웃이 곧 "세션 없음"이다.
///     해제·로그인 알림은 fire-and-forget publish이고 발신자는 origin으로 자기 메시지를 거른다.
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

    /// <summary>세션을 가진 프로세스가 없을 때 이만큼 기다린다. 8인 매치 전원이 부재여도 pass 한 번이 수십 초를 넘지 않게 짧게 둔다.</summary>
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly byte[] Delivered = [1];
    private static readonly byte[] Rejected = [0];

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

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

    public void AnnounceLogin(long playerId)
    {
        PublishBestEffort(LoginSubject, new SessionNotice { PlayerId = playerId, OriginNodeId = NodeId });
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
        try
        {
            byte[] reply = await natsClient.RequestAsync(
                DeliverSubjectPrefix + playerId,
                MessagePackSerializer.Serialize(request, SerializerOptions),
                RemoteTimeout);
            return reply.Length == 1 && reply[0] == Delivered[0];
        }
        catch (Exception ex)
        {
            // 타임아웃이 정상 경로다: 어느 프로세스에도 세션이 없다. 그 밖의 실패는 경고로 남긴다.
            if (ex is TimeoutException or NATS.Client.NATSTimeoutException)
                logger.LogInformation(
                    "No user server owns the session; delivery dropped: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    op, playerId, matchingId);
            else
                logger.LogWarning(ex,
                    "Remote session delivery failed: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    op, playerId, matchingId);
            return false;
        }
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

        IMatchingSessionEndpoint? local = getLocalSession(request.PlayerId);
        if (local == null)
            return Task.FromResult<byte[]?>(null);

        using Packet packet = Packet.CreateForSending(request.PacketWire);
        bool delivered = Apply(local, request.Op, request.MatchingId, request.RequestId, packet);
        logger.LogInformation(
            "Remote session delivery handled: Op={Op}, PlayerId={PlayerId}, MatchingId={MatchingId}, From={Origin}, Delivered={Delivered}",
            request.Op, request.PlayerId, request.MatchingId, request.OriginNodeId, delivered);
        return Task.FromResult<byte[]?>(delivered ? Delivered : Rejected);
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

        logger.LogWarning(
            "Session superseded by a login on another user server: PlayerId={PlayerId}, NewOwner={Origin}",
            notice.PlayerId, notice.OriginNodeId);
        local.DisconnectForDuplicateLogin();
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
}
