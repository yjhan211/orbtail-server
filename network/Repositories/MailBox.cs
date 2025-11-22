using MessagePack;
using network.interfaces;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class MailBox
{
    public void AddMail(MailInfo mailInfo)
    {
        MailDict.Add(mailInfo.MailUid, mailInfo);
    }

    public void AddMail(List<MailInfo> mailInfos)
    {
        foreach (var mailInfo in mailInfos)
        {
            MailDict.Add(mailInfo.MailUid, mailInfo);
        }
    }

    public void DeleteMail(long mailId)
    {
        MailDict.Remove(mailId);
    }

    public void DeleteAllMails()
    {
        MailDict = [];
    }

    public async Task Save(ICacheHelper cacheHelper)
    {
        await cacheHelper.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<MailBox> Load(ICacheHelper cacheHelper, long playerId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return new MailBox(playerId);

        var mails = MessagePackSerializer.Deserialize<MailBox>(serializedData);
        return mails;
    }

    public static async Task Delete(ICacheHelper cacheHelper, long playerId)
    {
        await cacheHelper.HashDeleteAsync(HashKey, playerId);
    }
}
