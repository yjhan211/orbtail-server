namespace user_server.infrastructure.exceptions;

/// <summary>
/// Base exception for infrastructure-related errors
/// </summary>
public abstract class InfrastructureException : Exception
{
    public string ErrorCode { get; }

    protected InfrastructureException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    protected InfrastructureException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Exception thrown when Redis operation fails
/// </summary>
public class CacheOperationException : InfrastructureException
{
    public string Operation { get; }

    public CacheOperationException(string operation, Exception innerException)
        : base("CACHE_OPERATION_FAILED",
            $"Cache operation '{operation}' failed: {innerException.Message}",
            innerException)
    {
        Operation = operation;
    }
}

/// <summary>
/// Exception thrown when NATS operation fails
/// </summary>
public class MessagingException(string subject, Exception innerException) : InfrastructureException("MESSAGING_FAILED",
    $"Messaging operation on subject '{subject}' failed: {innerException.Message}",
    innerException)
{
    public string Subject { get; } = subject;
}

/// <summary>
/// Exception thrown when network connection fails
/// </summary>
public class NetworkConnectionException(string remoteEndpoint, Exception innerException) : InfrastructureException(
    "NETWORK_CONNECTION_FAILED",
    $"Network connection to '{remoteEndpoint}' failed: {innerException.Message}",
    innerException)
{
    public string RemoteEndpoint { get; } = remoteEndpoint;
}
