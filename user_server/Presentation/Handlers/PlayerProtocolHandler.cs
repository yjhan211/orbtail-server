using network.common;
using network.common.data;
using network.common.data.models;
using user_server.application.commands;
using user_server.application.commands.player;

namespace user_server.presentation.handlers;

/// <summary>
/// Presentation layer handler for player-related protocol messages
/// Translates protocol messages into commands
/// </summary>
public class PlayerProtocolHandler
{
    private readonly ICommandHandler<MoveCommand> _moveHandler;
    private readonly ICommandHandler<WearItemCommand> _wearItemHandler;
    private readonly ICommandHandler<UseItemCommand> _useItemHandler;
    private readonly ICommandHandler<ExploreCommand> _exploreHandler;
    private readonly ICommandHandler<IncreaseQuestCountCommand> _increaseQuestHandler;
    private readonly ICommandHandler<CompleteQuestCommand> _completeQuestHandler;
    private readonly ICommandHandler<SendMailCommand> _sendMailHandler;
    private readonly ICommandHandler<ReceiveMailCommand> _receiveMailHandler;
    private readonly ICommandHandler<PerformSocialActionCommand> _socialActionHandler;
    private readonly ICommandHandler<SetPlayerNameCommand> _setNameHandler;
    private readonly ICommandHandler<ChangeMapCommand> _changeMapHandler;

    public PlayerProtocolHandler(
        ICommandHandler<MoveCommand> moveHandler,
        ICommandHandler<WearItemCommand> wearItemHandler,
        ICommandHandler<UseItemCommand> useItemHandler,
        ICommandHandler<ExploreCommand> exploreHandler,
        ICommandHandler<IncreaseQuestCountCommand> increaseQuestHandler,
        ICommandHandler<CompleteQuestCommand> completeQuestHandler,
        ICommandHandler<SendMailCommand> sendMailHandler,
        ICommandHandler<ReceiveMailCommand> receiveMailHandler,
        ICommandHandler<PerformSocialActionCommand> socialActionHandler,
        ICommandHandler<SetPlayerNameCommand> setNameHandler,
        ICommandHandler<ChangeMapCommand> changeMapHandler)
    {
        _moveHandler = moveHandler;
        _wearItemHandler = wearItemHandler;
        _useItemHandler = useItemHandler;
        _exploreHandler = exploreHandler;
        _increaseQuestHandler = increaseQuestHandler;
        _completeQuestHandler = completeQuestHandler;
        _sendMailHandler = sendMailHandler;
        _receiveMailHandler = receiveMailHandler;
        _socialActionHandler = socialActionHandler;
        _setNameHandler = setNameHandler;
        _changeMapHandler = changeMapHandler;
    }

    public async Task HandleMove(long playerId, C_TO_U_MOVE body)
    {
        await _moveHandler.HandleAsync(new MoveCommand(playerId, body));
    }

    public async Task HandleWearItem(long playerId, C_TO_U_WEAR_ITEM body)
    {
        await _wearItemHandler.HandleAsync(new WearItemCommand(playerId, body));
    }

    public async Task HandleUseItem(long playerId, C_TO_U_USE_ITEM body)
    {
        await _useItemHandler.HandleAsync(new UseItemCommand(playerId, body));
    }

    public async Task HandleExplore(long playerId, C_TO_U_EXPLORE body)
    {
        await _exploreHandler.HandleAsync(new ExploreCommand(playerId, body));
    }

    public async Task HandleIncreaseQuestCount(long playerId, C_TO_U_QUEST_INCREASE body)
    {
        await _increaseQuestHandler.HandleAsync(new IncreaseQuestCountCommand(playerId, body));
    }

    public async Task HandleCompleteQuest(long playerId, C_TO_U_QUEST_SUCCESS body)
    {
        await _completeQuestHandler.HandleAsync(new CompleteQuestCommand(playerId, body));
    }

    public async Task HandleSendMail(long playerId, MailInfo mailInfo)
    {
        await _sendMailHandler.HandleAsync(new SendMailCommand(playerId, mailInfo));
    }

    public async Task HandleReceiveMail(long playerId, C_TO_U_MAIL_RECEIVE body)
    {
        await _receiveMailHandler.HandleAsync(new ReceiveMailCommand(playerId, body));
    }

    public async Task HandleSocialAction(long playerId, C_TO_U_SOCIAL_ACTION body)
    {
        await _socialActionHandler.HandleAsync(new PerformSocialActionCommand(playerId, body));
    }

    public async Task HandleSetName(long playerId, string name)
    {
        await _setNameHandler.HandleAsync(new SetPlayerNameCommand(playerId, name));
    }

    public async Task HandleChangeMap(long playerId, C_TO_U_CHANGE_MAP body)
    {
        await _changeMapHandler.HandleAsync(new ChangeMapCommand(playerId, body));
    }
}
