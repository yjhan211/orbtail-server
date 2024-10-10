using System.Threading.Channels;
using StackExchange.Redis;
using network.common;
using network.packets;
using network.managers;

namespace user_server.managers
{
    public class UpdateObjectManager : IDisposable
    {
        private readonly LogManager _logManager;
        private readonly CancellationTokenSource _cts;
        private readonly SendPacketDelegate _sendToClient;
        private readonly Channel<GameObjectInfo> _updateObjectChannel;
        private readonly Task _task;
        private bool _disposed;

        public UpdateObjectManager(CancellationTokenSource cts, LogManager logManager, SendPacketDelegate sendToClient, Channel<GameObjectInfo> updateObjectReader)
        {
            _cts = cts;
            _sendToClient = sendToClient;
            _logManager = logManager;
            _updateObjectChannel = updateObjectReader;
            _task = StartTask();
        }

        public void EnqueueUpdateObject(GameObjectInfo objectInfo)
        {
            _updateObjectChannel.Writer.TryWrite(objectInfo);
        }

        // 이 함수가 호출되는 경우: G_TO_U_SPAWN_LIST의 objectKeyList에는 있으나 클라에는 GameObjectInfo가 없을 때
        // 어떤 경우에 생기는가: 이미 스폰되어 있는 오브젝트를 만났을 때
        public async Task GetObjectInfo(GameUser _, C_TO_U_OBJECT_INFO body)
        {
            RedisValue[] keys = body.ObjectKeyList.ConvertAll(x => (RedisValue)x).ToArray();
            var object_info_list = await GameObjectInfo.LoadAll(keys);
            foreach (var object_info in object_info_list)
            {
                EnqueueUpdateObject(object_info);
            }
        }

        private Task StartTask()
        {
            return Task.Run(async () =>
            {
                try
                {
                    await RecvUpdateObjectTask();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    _logManager.WriteErrorLog(e);
                }
            }, _cts.Token);
        }

        private async Task RecvUpdateObjectTask()
        {
            while (await _updateObjectChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                List<GameObjectInfo> updateObjectList = new();
                while (updateObjectList.Count < Config.BROADCAST_UNIT && _updateObjectChannel.Reader.TryRead(out var updateObjectInfo))
                {
                    updateObjectList.Add(updateObjectInfo);
                }

                if (updateObjectList.Count > 0)
                {
                    using var packet = PacketMaker.U_TO_C_MAP_UPDATE(updateObjectList, DateTime.UtcNow);
                    _sendToClient(packet);
                }
            }
        }

        public async Task StopUpdateObjectTask()
        {
            if (_task != null)
            {
                await _task;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                StopUpdateObjectTask().GetAwaiter().GetResult();
                _cts.Dispose();
                _updateObjectChannel.Writer.Complete();
            }

            _disposed = true;
        }
    }
}
