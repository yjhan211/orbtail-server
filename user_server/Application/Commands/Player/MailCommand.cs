using network.common.data;
using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to send a mail to player
/// </summary>
public record SendMailCommand(
    long PlayerId,
    MailInfo MailInfo
);

/// <summary>
/// Command to receive (open) mail
/// </summary>
public record ReceiveMailCommand(
    long PlayerId,
    C_TO_U_MAIL_RECEIVE ReceiveData
);
