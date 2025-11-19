namespace user_server.domain.exceptions;

/// <summary>
/// Base exception for all domain-related errors
/// </summary>
public abstract class DomainException : Exception
{
    /// <summary>
    /// Error code for logging and tracking
    /// </summary>
    public string ErrorCode { get; }

    protected DomainException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    protected DomainException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
