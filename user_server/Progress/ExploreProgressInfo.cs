using network.common.data.models;
using network.interfaces;

namespace user_server.progress;

public class ExploreProgressInfo(ExploreTargetInfo exploreTargetInfo) : IProgressTrackable
{
    public ExploreTargetInfo ExploreTargetInfo { get; } = exploreTargetInfo;

    public DateTime GetEndTime()
    {
        return ExploreTargetInfo.EndTimestamp;
    }
}