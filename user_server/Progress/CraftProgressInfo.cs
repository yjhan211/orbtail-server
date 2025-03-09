using network.interfaces;

namespace user_server.progress;

public class CraftProgressInfo(int craftId, DateTime endTimestamp) : IProgressTrackable
{
    public readonly int CraftId = craftId;

    public DateTime GetEndTime()
    {
        return endTimestamp;
    }
}