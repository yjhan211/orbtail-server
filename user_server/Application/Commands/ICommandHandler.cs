namespace user_server.application.commands;

/// <summary>
/// Base interface for command handlers
/// </summary>
/// <typeparam name="TCommand">The command type to handle</typeparam>
public interface ICommandHandler<in TCommand>
{
    /// <summary>
    /// Handles the command asynchronously
    /// </summary>
    /// <param name="command">The command to handle</param>
    /// <returns>Task representing the asynchronous operation</returns>
    Task HandleAsync(TCommand command);
}
