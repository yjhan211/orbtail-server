using network.common;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace user_server.controllers;

public static class MailBoxController
{
    private const string MailUidKey = "mail_uid_key";

    public static async Task<MailInfo> CreateMail(int mailId, List<(int, int)>? items = null)
    {
        // TODO RDB PK로 교체 예정
        var mailUid = await CacheHelper.Instance.StringIncrementAsync(MailUidKey);
        return new MailInfo(mailUid, mailId, items ?? []);
    }

    public static async Task SendMail(GameUser user, MailInfo mailInfo)
    {
        var mailBox = await MailBox.Load(user.PlayerId);
        mailBox.AddMail(mailInfo);
        await mailBox.Save();

        // await GetCurrentMailList(user);
    }

    public static async Task ReceiveMail(GameUser user, C_TO_U_MAIL_RECEIVE body)
    {
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            var mailBox = await MailBox.Load(user.PlayerId);
            if (!mailBox.MailDict.TryGetValue(body.MailUid, out var mailInfo))
            {
                throw new Exception($"Invalid Mail Info. mailUid: {body.MailUid}");
            }

            if (mailInfo.State == MailState.REWARDED)
            {
                throw new Exception($"Already Rewarded. mailUid: {body.MailUid}");
            }

            var playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null)
            {
                throw new Exception($"Invalid Player Id. playerId: {user.PlayerId}");
            }
            
            foreach (var (itemId, count) in mailInfo.Items)
            {
                var rewardItem = await InventoryController.CreateItem(itemId, count);
                playerInfo.InventoryInfo.AddItem(rewardItem);
            }
            
            await mailBox.Save();
            await playerInfo.Save();
        }

        using var packet = PacketMaker.U_TO_C_MAIL_RECEIVE(body.MailUid, ErrorCode.SUCCESS);
        user.Send(packet);

        await GetCurrentMailList(user);
    }
    
    public static async Task GetCurrentMailList(GameUser user)
    {
        var mailBox = await MailBox.Load(user.PlayerId);
        user.LogManager.WriteDebugLog($"Mail Count : {mailBox.MailDict.Count}");
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
