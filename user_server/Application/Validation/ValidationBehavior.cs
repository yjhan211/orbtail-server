using FluentValidation;

namespace user_server.application.validation;

/// <summary>
/// Generic validation behavior that can be used with command handlers
/// </summary>
public class ValidationBehavior<TCommand>
{
    private readonly IValidator<TCommand>? _validator;

    public ValidationBehavior(IValidator<TCommand>? validator = null)
    {
        _validator = validator;
    }

    /// <summary>
    /// Validates a command and throws ValidationException if invalid
    /// </summary>
    public async Task ValidateAsync(TCommand command)
    {
        if (_validator == null)
        {
            return; // No validator registered, skip validation
        }

        var validationResult = await _validator.ValidateAsync(command);

        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }
    }

    /// <summary>
    /// Validates a command and returns validation result
    /// </summary>
    public async Task<FluentValidation.Results.ValidationResult> ValidateAndReturnResultAsync(TCommand command)
    {
        if (_validator == null)
        {
            return new FluentValidation.Results.ValidationResult(); // Empty (valid) result
        }

        return await _validator.ValidateAsync(command);
    }
}
