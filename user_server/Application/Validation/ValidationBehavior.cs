using FluentValidation;

namespace user_server.application.validation;

public class ValidationBehavior<TCommand>(IValidator<TCommand>? validator = null)
{
    public async Task ValidateAsync(TCommand command)
    {
        if (validator == null)
        {
            return;
        }

        var validationResult = await validator.ValidateAsync(command);

        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }
    }

    public async Task<FluentValidation.Results.ValidationResult> ValidateAndReturnResultAsync(TCommand command)
    {
        if (validator == null)
        {
            return new FluentValidation.Results.ValidationResult();
        }

        return await validator.ValidateAsync(command);
    }
}
