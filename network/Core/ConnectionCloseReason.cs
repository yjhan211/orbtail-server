namespace network.core;

internal enum ConnectionCloseReason
{
    RemoteClosed, // 클라이언트 종료
    ReceiveError, // 수신 중 오류
    MalformedPacket, // 잘못된 패킷
    MessageQueueOverflow, // 수신 버퍼 초과
    SendError, // 송신 중 오류
    SendQueueOverflow, // 송신 버퍼 초과
    ExplicitDisconnect, // 서버코드 내 명시적 종료
    ServerStopping, // 서버 종료 중 연결 정리
    SessionCreationFailed, // 세션 생성 실패
    AuthenticationTimeout, // 제한시간 내 인증하지 않음
    AuthenticatedIdleTimeout // 하트비트 끊김
}
