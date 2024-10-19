using RedLockNet.SERedis;
using network.common;
using user_server.managers;

namespace user_server.handlers
{
    public class EnvironmentHandler
    {
        private readonly CancellationTokenSource _cts;
        private readonly RedLockFactory _redLock;
        private readonly SendPacketDelegate _sendToClient;
        private readonly GameObjectInfo _objectInfo;
        private Task? _task;
        private bool _disposed;
        private bool _stopping;

        public EnvironmentHandler(CancellationTokenSource cts, RedLockFactory redLock, SendPacketDelegate sendToClient, GameObjectInfo objectInfo)
        {
            _cts = cts;
            _redLock = redLock;
            _sendToClient = sendToClient;
            _objectInfo = objectInfo;

            // _task = Initialize();
        }

        public async Task StartAsync()
        {
            _task = Initialize();
            await Task.Delay(100);
            if (_task.Status == TaskStatus.Created)
            {
                throw new InvalidOperationException("Initialize task did not start");
            }
        }

        public Task ProcessAsync(object request) => Task.CompletedTask;
        public Task Initialize()
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        // await ProcessMapEnvironmentTask();
                        await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
                    }
                }
                catch (Exception e)
                {
                    throw;
                }
            }, _cts.Token);
        }

        // private async Task ProcessMapEnvironmentTask()
        // {
        //     switch (_objectInfo.MapId)
        //     {
        //         case MapID.WETLAND_1:
        //             if (DateTime.UtcNow < _objectInfo.DebuffTimestamp)
        //             {
        //                 return;
        //             }

        //             // var playerId = _objectInfo.ObjectId;
        //             // using (await PlayerInfo.Lock(_redLock, _objectInfo.ObjectId))
        //             // {
        //             //     var playerInfo = await PlayerInfo.Load(playerId);
        //             //     if (playerInfo == null)
        //             //     {
        //             //         throw new Exception("cannot found player info");
        //             //     }

        //             //     if (playerInfo.WearItemIdList.Contains(103000004))
        //             //     {
        //             //         return;
        //             //     }

        //             //     playerInfo.JobInfo.Hp -= 1;
        //             //     await playerInfo.Save();

        //             //     using var updateHpPacket = PacketMaker.U_TO_C_UPDATE_HP(-1, playerInfo.JobInfo.Hp);
        //             //     _sendToClient(updateHpPacket);
        //             // }

        //             _objectInfo.DebuffTimestamp = _objectInfo.DebuffTimestamp.AddSeconds(5);
        //             break;

        //         default:
        //             break;
        //     }
        // }

        public async Task StopAsync()
        {
            if (_stopping || _task == null)
            {
                return;
            }

            _stopping = true;
            _cts.Cancel();
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {

            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                StopAsync().GetAwaiter().GetResult();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
