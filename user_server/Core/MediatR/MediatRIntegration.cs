using MediatR;
using user_server.application.commands.player;
using user_server.application.queries.player;

namespace user_server.core.mediatr;

/// <summary>
/// MediatR integration for Commands - these implement IRequest pattern
/// Commands are now compatible with MediatR
/// </summary>
public record MediatRMoveCommand(long PlayerId, network.common.data.models.C_TO_U_MOVE MoveData)
    : IRequest;

public record MediatRWearItemCommand(long PlayerId, network.common.data.models.C_TO_U_WEAR_ITEM WearData)
    : IRequest;

/// <summary>
/// MediatR integration for Queries - these implement IRequest<TResponse> pattern
/// Queries return specific result types
/// </summary>
public record MediatRGetPlayerInfoQuery(long PlayerId)
    : IRequest<PlayerInfoResult>;

public record MediatRGetPlayerQuestsQuery(long PlayerId)
    : IRequest<PlayerQuestsResult>;

/// <summary>
/// Example usage note:
///
/// Current approach (direct):
///   await _commandHandler.HandleAsync(new MoveCommand(playerId, data));
///
/// MediatR approach (optional):
///   await _mediator.Send(new MediatRMoveCommand(playerId, data));
///
/// Benefits of MediatR:
/// - Automatic handler resolution
/// - Built-in pipeline behaviors (logging, validation, etc.)
/// - Decoupled sender/handler relationship
///
/// The existing Command/Query/Handler infrastructure works perfectly fine.
/// MediatR is provided as an optional enhancement for teams familiar with it.
/// </summary>
public static class MediatRNotes
{
    // This class exists purely for documentation purposes
}
