using network.common;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace user_server.players;

public class PlayerMailBox(GameUser user, PlayerInfo playerInfo, PlayerInventory playerInventory)
{
    private const string MailUidKey = "mail_uid_key";
    
    private readonly CacheHelper _cacheHelper = user.CacheHelper;
    private readonly SendPacketDelegate _sendToClient = user.Send;

    public static async Task<MailInfo> CreateMail(CacheHelper cacheHelper, int mailId, List<(int, int)>? items = null)
    {
        // TODO RDB PK로 교체 예정
        var mailUid = await cacheHelper.StringIncrementAsync(MailUidKey);
        return new MailInfo(mailUid, mailId, items ?? []);
    }

    public async Task SendMail(MailInfo mailInfo)
    {
        var mailBox = await MailBox.Load(_cacheHelper, playerInfo.PlayerId);
        mailBox.AddMail(mailInfo);
        await mailBox.Save(_cacheHelper);
    }

    public async Task ReceiveMail(C_TO_U_MAIL_RECEIVE body)
    {
        await using (await PlayerInfo.Lock(user.RedLock, playerInfo.PlayerId))
        {
            var mailBox = await MailBox.Load(_cacheHelper, playerInfo.PlayerId);
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
            await playerInfo.Save(_cacheHelper);
        }

        using var packet = PacketMaker.U_TO_C_MAIL_RECEIVE(body.MailUid, ErrorCode.SUCCESS);
        user.Send(packet);

        await SendCurrentMails();
    }
    
    public async Task SendCurrentMails()
    {
        var mailBox = await MailBox.Load(_cacheHelper, playerInfo.PlayerId);
        if (mailBox.MailDict.Count == 0)
        {
            return;
        }

        var mailKeys = mailBox.MailDict.Keys.ToArray();
        for (var i = 0; i < mailKeys.Length; i += Config.BROADCAST_UNIT)
        {
            var batchDict = mailKeys.Skip(i).Take(Config.BROADCAST_UNIT)
                .ToDictionary(key => key, key => mailBox.MailDict[key]);

            var isEnded = i + Config.BROADCAST_UNIT >= mailKeys.Length;

            using var packet = PacketMaker.U_TO_C_MAIL_LIST(batchDict, isEnded);
            user.Send(packet);
        }
    }
}
