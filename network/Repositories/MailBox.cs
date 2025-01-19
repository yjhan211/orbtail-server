using MessagePack;
using network.helpers;

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

    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<MailBox> Load(long playerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return new MailBox(playerId);
        
        var mails = MessagePackSerializer.Deserialize<MailBox>(serializedData);
        return mails;
    }
    
    public static async Task Delete(long playerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}
