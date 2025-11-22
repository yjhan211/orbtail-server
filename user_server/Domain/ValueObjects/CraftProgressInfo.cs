using network.interfaces;

namespace user_server.domain.valueobjects;

public class CraftProgressInfo(int craftId, DateTime endTimestamp) : IProgressTrackable
{
    public readonly int CraftId = craftId;

    public DateTime GetEndTime()
    {
        return endTimestamp;
    }
}