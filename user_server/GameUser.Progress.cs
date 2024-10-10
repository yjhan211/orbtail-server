using network.common;
using network.interfaces;
using user_server.controllers;
using user_server.managers;

namespace user_server
{
    public partial class GameUser : IPeer
    {
        public void StartExplore(ExploreTargetInfo exploreTargetInfo)
        {
            var exploreProgressInfo = new ExploreProgressInfo(exploreTargetInfo);
            _progressManager.AddProgressItem(
                exploreProgressInfo,
                async (trackable) =>
                {
                    var progressInfo = (ExploreProgressInfo)trackable;
                    await JobController.ExploreEnd(this, progressInfo.ExploreTargetInfo);
                }
            );
        }

        public void StartJobSkill(JobResourceInfo jobResourceInfo)
        {
            var jobProgressInfo = new JobProgressInfo(jobResourceInfo);
            _progressManager.AddProgressItem(
                jobProgressInfo,
                async (trackable) =>
                {
                    var progressInfo = (JobProgressInfo)trackable;
                    await JobController.JobSkillEnd(this, progressInfo.JobResourceInfo);
                }
            );
        }
    }
}