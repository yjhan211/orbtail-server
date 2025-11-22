using FluentValidation;
using user_server.application.commands.player;

namespace user_server.application.validation;

/// <summary>
/// Validator for MoveCommand
/// </summary>
public class MoveCommandValidator : AbstractValidator<MoveCommand>
{
    public MoveCommandValidator()
    {
        RuleFor(x => x.PlayerId)
            .GreaterThan(0)
            .WithMessage("Player ID must be greater than 0");

        RuleFor(x => x.MoveData)
            .NotNull()
            .WithMessage("Move data cannot be null");
    }
}

/// <summary>
/// Validator for WearItemCommand
/// </summary>
public class WearItemCommandValidator : AbstractValidator<WearItemCommand>
{
    public WearItemCommandValidator()
    {
        RuleFor(x => x.PlayerId)
            .GreaterThan(0)
            .WithMessage("Player ID must be greater than 0");

        RuleFor(x => x.WearData)
            .NotNull()
            .WithMessage("Wear data cannot be null");

        RuleFor(x => x.WearData.ItemUidList)
            .NotEmpty()
            .When(x => x.WearData != null)
            .WithMessage("Item UID list cannot be empty");
    }
}

/// <summary>
/// Validator for UseItemCommand
/// </summary>
public class UseItemCommandValidator : AbstractValidator<UseItemCommand>
{
    public UseItemCommandValidator()
    {
        RuleFor(x => x.PlayerId)
            .GreaterThan(0)
            .WithMessage("Player ID must be greater than 0");

        RuleFor(x => x.UseData)
            .NotNull()
            .WithMessage("Use data cannot be null");

        RuleFor(x => x.UseData.ItemUid)
            .GreaterThan(0)
            .When(x => x.UseData != null)
            .WithMessage("Item UID must be greater than 0");
    }
}

/// <summary>
/// Validator for CompleteQuestCommand
/// </summary>
public class CompleteQuestCommandValidator : AbstractValidator<CompleteQuestCommand>
{
    public CompleteQuestCommandValidator()
    {
        RuleFor(x => x.PlayerId)
            .GreaterThan(0)
            .WithMessage("Player ID must be greater than 0");

        RuleFor(x => x.QuestData)
            .NotNull()
            .WithMessage("Quest data cannot be null");

        RuleFor(x => x.QuestData.QuestId)
            .GreaterThan(0)
            .When(x => x.QuestData != null)
            .WithMessage("Quest ID must be greater than 0");
    }
}

/// <summary>
/// Validator for StartQuestCommand
/// </summary>
public class StartQuestCommandValidator : AbstractValidator<StartQuestCommand>
{
    public StartQuestCommandValidator()
    {
        RuleFor(x => x.PlayerId)
            .GreaterThan(0)
            .WithMessage("Player ID must be greater than 0");

        RuleFor(x => x.QuestId)
            .GreaterThan(0)
            .WithMessage("Quest ID must be greater than 0");
    }
}

/// <summary>
/// Validator for SetPlayerNameCommand
/// </summary>
public class SetPlayerNameCommandValidator : AbstractValidator<SetPlayerNameCommand>
{
    public SetPlayerNameCommandValidator()
    {
        RuleFor(x => x.PlayerId)
            .GreaterThan(0)
            .WithMessage("Player ID must be greater than 0");

        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Name cannot be empty")
            .MaximumLength(50)
            .WithMessage("Name cannot exceed 50 characters")
            .Matches("^[a-zA-Z0-9가-힣_]+$")
            .WithMessage("Name can only contain letters, numbers, Korean characters, and underscores");
    }
}
