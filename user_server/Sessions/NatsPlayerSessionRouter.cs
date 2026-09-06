using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.messaging;
using network.packets;

namespace user_server.sessions;

/// <summary>
///     플레이어가 어느 User Server에 접속해 있든 매칭 결과와 세션 알림을 전달한다.
///     현재 서버에 세션이 있으면 직접 처리하고, 없으면 NATS로 다른 서버에 전달을 요청한다.
///     패킷 바이트가 아닌 결과 데이터를 전달하며, 대상 세션을 가진 서버가 클라이언트 패킷을 만들어 응답한다.
///     원격 요청은 한 번만 보낸다. 응답을 확인하지 못하면 false를 반환하고 호출자가 실패 정리를 진행한다.
/// </summary>
internal sealed class NatsPlayerSessionRouter(
    INatsClient natsClient,
    Func<long, IMatchingSessionEndpoint?> getLocalSession,
    string nodeId,
    ILogger logger) : IPlayerSessionRouter
{
    private const string MatchingSuccessSubject = "user_server.session.deliver.matching_success";
    private const string MatchingFailedSubject = "user_server.session.deliver.matching_failed";
    private const string AdmissionFailedSubject = "user_server.session.deliver.admission_failed";
    private const string ClearSubject = "user_server.session.clear";
    private const string LoginSubject = "user_server.session.login";

    private static readonly TimeSpan RemoteDeliveryTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly byte[] Delivered = [1];
    private static readonly byte[] Rejected = [0];

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private string NodeId { get; } = nodeId;

    public void Start()
    {
        natsClient.SubscribeRequest(MatchingSuccessSubject, HandleMatchingSuccessAsync);
        natsClient.SubscribeRequest(MatchingFailedSubject, HandleMatchingFailedAsync);
        natsClient.SubscribeRequest(AdmissionFailedSubject, HandleAdmissionFailedAsync);
        natsClient.Subscribe(ClearSubject, (_, body) => HandleClear(body));
        natsClient.Subscribe(LoginSubject, (_, body) => HandleLogin(body));
    }

    public Task<bool> DeliverMatchingSuccessAsync(long playerId, string requestId, U_TO_C_MATCHING_SUCCESS result)
    {
        var local = getLocalSession(playerId);
        if (local != null)
        {
            using var packet = CreateMatchingSuccessPacket(result);
            return Task.FromResult(local.TryDeliverMatchingSuccess(result.MatchingId, requestId, packet));
        }

        var request = new U_TO_U_MATCHING_SUCCESS
        {
            PlayerId = playerId,
            RequestId = requestId,
            Result = result,
            OriginNodeId = NodeId
        };
        return DeliverRemoteAsync(MatchingSuccessSubject, playerId, result.MatchingId, request);
    }

    public Task<bool> DeliverMatchingFailedAsync(long playerId, long matchingId, string requestId, ErrorCode errorCode)
    {
        var local = getLocalSession(playerId);
        if (local != null)
        {
            using var packet = CreateMatchingFailedPacket(errorCode, matchingId);
            return Task.FromResult(local.TryDeliverMatchingFailed(matchingId, requestId, packet));
        }

        var request = new U_TO_U_MATCHING_FAILED
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            RequestId = requestId,
            ErrorCode = errorCode,
            OriginNodeId = NodeId
        };
        return DeliverRemoteAsync(MatchingFailedSubject, playerId, matchingId, request);
    }

    public Task<bool> DeliverAdmissionFailedAsync(long playerId, long matchingId, ErrorCode errorCode)
    {
        var local = getLocalSession(playerId);
        if (local != null)
        {
            using var packet = CreateMatchingFailedPacket(errorCode, matchingId);
            return Task.FromResult(local.TryDeliverAdmissionFailed(matchingId, packet));
        }

        var request = new U_TO_U_ADMISSION_FAILED
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            ErrorCode = errorCode,
            OriginNodeId = NodeId
        };
        return DeliverRemoteAsync(AdmissionFailedSubject, playerId, matchingId, request);
    }

    public void ClearMatchingAssignment(long playerId, long matchingId)
    {
        getLocalSession(playerId)?.ClearMatchingAssignment(matchingId);
        PublishNotice(ClearSubject, playerId, new U_TO_U_MATCHING_ASSIGNMENT_CLEAR
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            OriginNodeId = NodeId
        });
    }

    public void AnnounceLogin(long playerId, long generation)
    {
        PublishNotice(LoginSubject, playerId, new U_TO_U_SESSION_LOGIN
        {
            PlayerId = playerId,
            OriginNodeId = NodeId,
            SessionGeneration = generation
        });
    }

    private async Task<bool> DeliverRemoteAsync<T>(string subject, long playerId, long matchingId, T request)
    {
        byte[] requestBody = MessagePackSerializer.Serialize(request, SerializerOptions);
        try
        {
            byte[] reply = await natsClient.RequestAsync(subject, requestBody, RemoteDeliveryTimeout);
            return reply.Length == 1 && reply[0] == Delivered[0];
        }
        catch (Exception ex)
        {
            if (IsTimeout(ex))
            {
                logger.LogInformation(
                    "Remote session delivery was not confirmed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    subject, playerId, matchingId);
            }
            else
            {
                logger.LogWarning(ex,
                    "Remote session delivery failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    subject, playerId, matchingId);
            }
            return false;
        }
    }

    private Task<byte[]?> HandleMatchingSuccessAsync(string subject, byte[] body, CancellationToken cancellationToken)
    {
        var request = TryReadMessage<U_TO_U_MATCHING_SUCCESS>(body, subject);
        if (request == null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return HandleDelivery(subject, request.OriginNodeId, request.PlayerId, request.Result.MatchingId,
            () => CreateMatchingSuccessPacket(request.Result),
            (session, packet) => session.TryDeliverMatchingSuccess(request.Result.MatchingId, request.RequestId, packet));
    }

    private Task<byte[]?> HandleMatchingFailedAsync(string subject, byte[] body, CancellationToken cancellationToken)
    {
        var request = TryReadMessage<U_TO_U_MATCHING_FAILED>(body, subject);
        if (request == null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return HandleDelivery(subject, request.OriginNodeId, request.PlayerId, request.MatchingId,
            () => CreateMatchingFailedPacket(request.ErrorCode, request.MatchingId),
            (session, packet) => session.TryDeliverMatchingFailed(request.MatchingId, request.RequestId, packet));
    }

    private Task<byte[]?> HandleAdmissionFailedAsync(string subject, byte[] body, CancellationToken cancellationToken)
    {
        var request = TryReadMessage<U_TO_U_ADMISSION_FAILED>(body, subject);
        if (request == null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return HandleDelivery(subject, request.OriginNodeId, request.PlayerId, request.MatchingId,
            () => CreateMatchingFailedPacket(request.ErrorCode, request.MatchingId),
            (session, packet) => session.TryDeliverAdmissionFailed(request.MatchingId, packet));
    }

    private Task<byte[]?> HandleDelivery(string subject, string originNodeId, long playerId, long matchingId,
        Func<Packet> createPacket, Func<IMatchingSessionEndpoint, Packet, bool> deliver)
    {
        var localSession = getLocalSession(playerId);
        if (localSession == null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        using var packet = createPacket();
        bool delivered = deliver(localSession, packet);
        logger.LogInformation("Remote session delivery handled: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}, From={Origin}, Delivered={Delivered}",
            subject, playerId, matchingId, originNodeId, delivered);
        return Task.FromResult<byte[]?>(delivered ? Delivered : Rejected);
    }

    private static bool IsTimeout(Exception exception) => exception is TimeoutException or NATS.Client.NATSTimeoutException;

    private static Packet CreateMatchingSuccessPacket(U_TO_C_MATCHING_SUCCESS result) =>
        PacketMaker.U_TO_C_MATCHING_SUCCESS(
            result.MatchingId, result.GameServerIp, result.GameServerPort,
            result.GameEndTimestamp, result.GameHandoffTicket, result.PlayerRoster);

    private static Packet CreateMatchingFailedPacket(ErrorCode errorCode, long matchingId) =>
        PacketMaker.U_TO_C_MATCHING_FAILED(errorCode, matchingId);

    private void HandleClear(byte[] body)
    {
        var notice = TryReadMessage<U_TO_U_MATCHING_ASSIGNMENT_CLEAR>(body, ClearSubject);
        if (notice == null || notice.OriginNodeId == NodeId)
        {
            return;
        }
        getLocalSession(notice.PlayerId)?.ClearMatchingAssignment(notice.MatchingId);
    }

    private void HandleLogin(byte[] body)
    {
        var notice = TryReadMessage<U_TO_U_SESSION_LOGIN>(body, LoginSubject);
        if (notice == null || notice.OriginNodeId == NodeId)
        {
            return;
        }

        var local = getLocalSession(notice.PlayerId);
        local?.DisconnectIfOlderSession(notice.SessionGeneration);
    }

    private T? TryReadMessage<T>(byte[] body, string subject) where T : class
    {
        try
        {
            return MessagePackSerializer.Deserialize<T>(body, SerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            logger.LogWarning(ex, "Malformed session message: Subject={Subject}", subject);
            return null;
        }
    }

    private void PublishNotice<T>(string subject, long playerId, T notice)
    {
        try
        {
            natsClient.Publish(subject, MessagePackSerializer.Serialize(notice, SerializerOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session notice publish failed: Subject={Subject}, PlayerId={PlayerId}", subject, playerId);
        }
    }
}
