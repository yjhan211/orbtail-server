namespace user_server.Controller
{
    using game_server;
    using MessagePack;
    using network;
    using network.Common;
    using network.manager;
    using StackExchange.Redis;

    public class JobController
    {
        GameUser user;

        /*-------------------------------------------------------------*/

        JobInfo job_info;

        public JobController(GameUser user, JobInfo job_info)
        {
            this.user = user;
            this.job_info = job_info;
        }

        public async Task GetJob(long _, C_TO_U_GET_JOB body)
        {
            if (this.job_info!.job_type != JobType.NONE)
            {
                user.SendToClient(
                    PacketMaker.U_TO_C_GET_JOB(user.player_id, ErrorCode.ALREADY_HAS_JOB, job_info)
                );
                return;
            }

            switch (body.job_type)
            {
                case JobType.GEOIOGIST:
                    job_info.job_type = JobType.GEOIOGIST;
                    job_info.job_grade = JobGrade.TRAINEE;
                    break;

                default:
                    break;
            }

            await JobInfoController.Save(user.cache_helper, job_info);

            user.SendToClient(
                PacketMaker.U_TO_C_GET_JOB(user.player_id, ErrorCode.SUCCESS, job_info)
            );
        }
    }
}
