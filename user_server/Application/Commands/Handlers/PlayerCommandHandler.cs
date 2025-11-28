using network.common.data.models;
using user_server.application.commands.player;
using user_server.infrastructure.network;

namespace user_server.application.commands.handlers;

/// <summary>
/// Handles all player-related commands
/// This handler acts as a facade to the Player aggregate
/// </summary>
public class PlayerCommandHandler(Func<long, GameSession?> getSession) :
    ICommandHandler<WearItemCommand>,
    ICommandHandler<UseItemCommand>,
    ICommandHandler<IncreaseQuestCountCommand>,
    ICommandHandler<CompleteQuestCommand>,
    ICommandHandler<StartQuestCommand>,
    ICommandHandler<SendMailCommand>,
    ICommandHandler<ReceiveMailCommand>,
    ICommandHandler<PerformSocialActionCommand>,
    ICommandHandler<SetPlayerNameCommand>,
    ICommandHandler<ChangeMapCommand>,
    ICommandHandler<EnterMapCommand>,
    ICommandHandler<EnterCampCommand>
{
    public async Task HandleAsync(WearItemCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.WearItem(command.WearData);
    }

    public async Task HandleAsync(UseItemCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.UseItem(command.UseData);
    }

    public async Task HandleAsync(IncreaseQuestCountCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.IncreaseQuestCount(command.QuestData);
    }

    public async Task HandleAsync(CompleteQuestCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.CompleteQuest(command.QuestData);
    }

    public async Task HandleAsync(StartQuestCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.StartQuest(command.QuestId);
    }

    public async Task HandleAsync(SendMailCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.SendMail(command.MailInfo);
    }

    public async Task HandleAsync(ReceiveMailCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.ReceiveMail(command.ReceiveData);
    }

    public async Task HandleAsync(PerformSocialActionCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.PerformSocialAction(command.ActionData);
    }

    public async Task HandleAsync(SetPlayerNameCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.SetName(new C_TO_U_SET_NAME { Name = command.Name });
    }

    public async Task HandleAsync(ChangeMapCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.ChangeMap(command.MapData.MapId);
    }

    public async Task HandleAsync(EnterMapCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.EnterMap(command.MapId, command.SpawnPosition, command.IsFlip, command.IsLogin);
    }

    public async Task HandleAsync(EnterCampCommand command)
    {
        var session = getSession(command.PlayerId);
        if (session?.Player == null) return;

        await session.Player.EnterCamp(command.MapSubId);
    }
}
