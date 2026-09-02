namespace network.core;

internal enum ConnectionCloseReason
{
    RemoteClosed,
    ReceiveError,
    MalformedPacket,
    MessageQueueOverflow,
    SendError,
    SendQueueOverflow,
    ExplicitDisconnect,
    ServerStopping,
    SessionCreationFailed,
    AuthenticationTimeout,
    AuthenticatedIdleTimeout
}
