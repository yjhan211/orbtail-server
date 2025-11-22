namespace user_server.application.exceptions;

/// <summary>
/// Exception thrown when command or query validation fails
/// Note: FluentValidation also has ValidationException, so we namespace this carefully
/// </summary>
public class CommandValidationException : Exception
{
    public Dictionary<string, string[]> Errors { get; }

    public CommandValidationException(Dictionary<string, string[]> errors)
        : base("One or more validation errors occurred")
    {
        Errors = errors;
    }

    public CommandValidationException(string propertyName, string errorMessage)
        : base($"Validation failed for {propertyName}: {errorMessage}")
    {
        Errors = new Dictionary<string, string[]>
        {
            { propertyName, new[] { errorMessage } }
        };
    }
}
