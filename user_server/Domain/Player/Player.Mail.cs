using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Mail 관련 기능
/// </summary>
public partial class Player
{
    public async Task SendMail(MailInfo mailInfo)
    {
        await _mailBoxManager.SendMail(mailInfo);
    }

    public async Task SendCurrentMails()
    {
        await _mailBoxManager.SendCurrentMails();
    }

    public async Task ReceiveMail(C_TO_U_MAIL_RECEIVE body)
    {
        await _mailBoxManager.ReceiveMail(body);
    }
}
