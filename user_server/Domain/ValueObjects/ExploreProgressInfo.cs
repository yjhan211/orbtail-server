using network.common.data.models;
using network.interfaces;

namespace user_server.domain.valueobjects;

public class ExploreProgressInfo(ExploreTargetInfo exploreTargetInfo) : IProgressTrackable
{
    public ExploreTargetInfo ExploreTargetInfo { get; } = exploreTargetInfo;

    public DateTime GetEndTime()
    {
        return ExploreTargetInfo.EndTimestamp;
    }
}