using network.common.data.models;
using user_server.controllers;
using user_server.managers;

namespace user_server;

public partial class GameUser
{
    public void StartExplore(ExploreTargetInfo exploreTargetInfo)
    {
        var exploreProgressInfo = new ExploreProgressInfo(exploreTargetInfo);
        _progressManager.AddProgressItem(
            exploreProgressInfo,
            async trackable =>
            {
                var progressInfo = (ExploreProgressInfo)trackable;
                await ExploreController.ExploreEnd(this, progressInfo.ExploreTargetInfo);
            }
        );
    }

    public void StartCraft(int craftId, DateTime endTimestamp)
    {
        var craftProgressInfo = new CraftProgressInfo(craftId, endTimestamp);
        _progressManager.AddProgressItem(
            craftProgressInfo,
            async trackable =>
            {
                var progressInfo = (CraftProgressInfo)trackable;
                await CraftController.CraftEnd(this, progressInfo.CraftId);
            }
        );
    }
}