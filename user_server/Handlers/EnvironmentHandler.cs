using network.common;
using network.managers;
using RedLockNet.SERedis;
using user_server.managers;

namespace user_server.handlers;

public sealed class EnvironmentHandler(
    LogManager logManager,
    CancellationTokenSource cts,
    RedLockFactory redLock,
    SendPacketDelegate sendToClient,
    GameObjectInfo objectInfo)
{
    private readonly GameObjectInfo _objectInfo = objectInfo;
    private readonly RedLockFactory _redLock = redLock;
    private readonly SendPacketDelegate _sendToClient = sendToClient;
    private bool _disposed;
    private bool _stopping;
    private Task? _task;

    public async Task StartAsync()
    {
        _task = Initialize();
        await Task.Delay(100);
        if (_task.Status == TaskStatus.Created) throw new InvalidOperationException("Initialize task did not start");
    }

    private Task Initialize()
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                    // await ProcessMapEnvironmentTask();
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
            catch (Exception ex)
            {
                logManager.WriteErrorLog(ex);
            }
        }, cts.Token);
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

    private async Task StopAsync()
    {
        if (_stopping || _task == null) return;

        _stopping = true;
        await cts.CancelAsync();
        try
        {
            await _task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing) StopAsync().GetAwaiter().GetResult();

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
    }
}