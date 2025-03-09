using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.interfaces;
using network.packets;
using StackExchange.Redis;
using user_server.players;

namespace user_server.controllers;

public class MapObjectController : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly SendPacketDelegate _sendToClient;
    private readonly Channel<GameObjectInfo> _updateObjectChannel;
    private readonly Task _processingTask;
    private bool _disposed;

    public MapObjectController(GameUser user)
    {
        _cts = user.Cts;
        _logger = user.Logger;
        _sendToClient = user.Send;
        _updateObjectChannel = Channel.CreateUnbounded<GameObjectInfo>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        _cacheHelper = user.CacheHelper;
        _processingTask = StartProcessingTask();
    }

    public virtual void EnqueueUpdateObject(GameObjectInfo objectInfo)
    {
        if (_disposed)
        {
            return;
        }
        _updateObjectChannel.Writer.TryWrite(objectInfo);
    }
    
    /// 이 함수가 호출되는 경우: G_TO_U_SPAWN_LIST의 objectKeyList에는 있으나 클라에는 GameObjectInfo가 없을 때
    /// 어떤 경우에 생기는가: 이미 스폰되어 있는 오브젝트를 만났을 때
    public async Task GetObjectInfo(C_TO_U_OBJECT_INFO body)
    {
        if (_disposed) return;
        
        var keys = body.ObjectKeyList.ConvertAll(x => (RedisValue)x).ToArray();
        var objectInfoList = await GameObjectInfo.LoadAll(_cacheHelper, keys);
        
        foreach (var objectInfo in objectInfoList)
        {
            EnqueueUpdateObject(objectInfo);
        }
    }

    private Task StartProcessingTask()
    {
        return Task.Run(async () =>
        {
            try
            {
                await ProcessObjectUpdatesAsync();
            }
            catch (OperationCanceledException)
            {
                // 작업이 취소된 경우 - 정상적인 종료
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing objects");
            }
        }, _cts.Token);
    }
    
    private async Task ProcessObjectUpdatesAsync()
    {
        while (await _updateObjectChannel.Reader.WaitToReadAsync(_cts.Token))
        {
            var updateObjectList = new List<GameObjectInfo>();
            
            while (updateObjectList.Count < Config.BROADCAST_UNIT &&
                  _updateObjectChannel.Reader.TryRead(out var updateObjectInfo))
            {
                updateObjectList.Add(updateObjectInfo);
            }

            if (updateObjectList.Count <= 0)
            {
                continue;
            }
            
            using var packet = PacketMaker.U_TO_C_MAP_UPDATE(updateObjectList, DateTime.UtcNow);
            _sendToClient(packet);
        }
    }
    
    private async Task StopProcessingTaskAsync()
    {
        if (!_processingTask.IsCompleted)
        {
            await _processingTask;
        }
    }

    public void Dispose()
    {
        Dispose(true);
    }
    
    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 남은 아이템 모두 비우기
            while (_updateObjectChannel.Reader.TryRead(out _)) { }

            // 채널 종료
            _updateObjectChannel.Writer.Complete();
            
            // 처리 태스크 종료 대기
            StopProcessingTaskAsync().GetAwaiter().GetResult();
        }

        _disposed = true;
    }
}