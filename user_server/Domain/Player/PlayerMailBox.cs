using user_server.infrastructure.network;
using network.common;
using network.common.data.models;
using network.interfaces;
using network.packets;

namespace user_server.domain.player;

public class PlayerMailBox(GameSession user, PlayerInfo playerInfo)
{
    private const string MailUidKey = "mail_uid_key";

    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    private readonly SendPacketDelegate _sendToClient = user.Send;

    public static async Task<MailInfo> CreateMail(ICacheHelper cacheHelper, int mailId, List<(int, int)>? items = null)
    {
        // TODO RDB PK로 교체 예정
        var mailUid = await cacheHelper.StringIncrementAsync(MailUidKey);
        return new MailInfo(mailUid, mailId, items ?? []);
    }

    public async Task SendMail(MailInfo mailInfo)
    {
        playerInfo.MailBox.AddMail(mailInfo);
        await playerInfo.MailBox.Save(_cacheHelper);
    }

    public async Task ReceiveMail(C_TO_U_MAIL_RECEIVE body)
    {
        await using (await PlayerInfo.Lock(user.RedLock, playerInfo.PlayerId))
        {
            var mailBox = playerInfo.MailBox;
            if (!mailBox.MailDict.TryGetValue(body.MailUid, out var mailInfo))
            {
                throw new Exception($"Invalid Mail Info. mailUid: {body.MailUid}");
            }

            if (mailInfo.State == MailState.REWARDED)
            {
                throw new Exception($"Already Rewarded. mailUid: {body.MailUid}");
            }

            foreach (var (itemId, count) in mailInfo.Items)
            {
                var rewardItem = await PlayerInventory.CreateItem(_cacheHelper, itemId, count);
                playerInfo.InventoryInfo.AddItem(rewardItem);
            }

            await mailBox.Save(_cacheHelper);
        }

        using var packet = PacketMaker.U_TO_C_MAIL_RECEIVE(body.MailUid, ErrorCode.SUCCESS);
        _sendToClient(packet);

        await SendCurrentMails();
    }

    public Task SendCurrentMails()
    {
        var mailBox = playerInfo.MailBox;
        if (mailBox.MailDict.Count == 0)
        {
            return Task.CompletedTask;
        }

        var mailKeys = mailBox.MailDict.Keys.ToArray();
        for (var i = 0; i < mailKeys.Length; i += Config.BROADCAST_UNIT)
        {
            var batchDict = mailKeys.Skip(i).Take(Config.BROADCAST_UNIT)
                .ToDictionary(key => key, key => mailBox.MailDict[key]);

            var isEnded = i + Config.BROADCAST_UNIT >= mailKeys.Length;

            using var packet = PacketMaker.U_TO_C_MAIL_LIST(batchDict, isEnded);
            _sendToClient(packet);
        }

        return Task.CompletedTask;
    }
}
