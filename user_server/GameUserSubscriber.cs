namespace user_server
{
    using network;
    using MessagePack;
    using System.Collections.Concurrent;
    using StackExchange.Redis;
    using game_server;
    using RedLockNet.SERedis;

    public partial class GameUser : IPeer
    {
        Cell last_cell;
        GameObjectInfo object_info;

        Task move_object_task;
        public CancellationTokenSource cts;

        NatsClient nats_client;

        ConcurrentQueue<GameObjectInfo> move_object_queue;

        SemaphoreSlim object_lock;

        public async Task SubscribeGameServer(RedisValue message)
        {
            try
            {
                await this.player_lock.WaitAsync();

                Packet packet = new((byte[])message!);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                var body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.G_TO_U_MOVE:
                        await HandleMessage<G_TO_U_MOVE>(player_id, body, SubscribeMove);
                        break;

                    case PROTOCOL.G_TO_U_SPAWN:
                        await HandleMessage<G_TO_U_SPAWN>(player_id, body, SubscribeSpawn);
                        break;

                    case PROTOCOL.G_TO_U_DESTROY:
                        await HandleMessage<G_TO_U_DESTROY>(player_id, body, SubscribeDestroy);
                        break;
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
            finally
            {
                this.player_lock.Release();
            }
        }

        // 구독중인 Cell에 오는 Move 메시지를 취합하는 Task (오로지 모아서 보내는 목적)
        async Task RecvMoveObjectTask()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (this.player_id == 0)
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    List<GameObjectInfo> game_object_list = new();

                    while (this.move_object_queue.TryDequeue(out var object_info))
                    {
                        if (game_object_list.Count >= Config.BROADCAST_UNIT)
                        {
                            break;
                        }

                        game_object_list.Add(object_info);
                    }

                    if (game_object_list.Count > 0)
                    {
                        Packet packet = PacketMaker.U_TO_C_MAP_UPDATE(game_object_list);
                        this.SendToClient(packet);
                    }

                    await Task.Delay(100);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                    this.OnRemoved();
                }
            }

            try
            {
                this.move_object_task!.Wait();
            }
            catch (AggregateException e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        async Task RequestMove(long player_id, C_TO_U_MOVE body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            if (this.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                // 아직 이동이 완료되지 않음
                return;
            }

            var next_target_cell = MapHelper.CalcTargetCell(
                this.object_info.target_cell,
                body.direction
            );

            if (MapHelper.IsOutOfMapRange(next_target_cell))
            {
                // 맵 밖으로 벗어남
                return;
            }

            await Move(next_target_cell, body.direction);
        }

        async Task Move(Cell next_target_cell, DirectionType direction, bool all_bound = false)
        {
            await this.object_lock.WaitAsync();

            var current_manage_server = GetObjectManageServer(this.object_info.current_cell);
            var target_manage_server = GetObjectManageServer(this.object_info.target_cell);

            // 과거 위치 챙겨놓고
            this.last_cell = Cell.Clone(this.object_info.current_cell);

            // current_cell을 target_cell로 변경
            this.object_info.current_cell = Cell.Clone(this.object_info.target_cell);

            // target_cell을 새로운 target_cell로 변경 및 move_timestamp 업데이트
            this.object_info.move_timestamp = DateTime.UtcNow;
            this.object_info.target_cell = next_target_cell;
            if (direction != DirectionType.NONE)
            {
                this.object_info.SetFlip(direction);
            }

            await GameObjectController.Save(this.cache_helper, this.object_info);

            this.object_lock.Release();

            if (current_manage_server != target_manage_server)
            {
                // 과거 담당 서버에는 영역을 떠났다고 전송
                PublishLeave();
            }

            // 현재 담당 서버에 전송
            PublishMove();

            var last_bound_cell_list = all_bound
                ? new()
                : MapHelper.GetBoundCellList(this.last_cell);

            var current_bound_cell_list = MapHelper.GetBoundCellList(this.object_info.current_cell);

            // 현재 바운드 - 이전 바운드 = spawn 대상
            var object_spawn_list = current_bound_cell_list
                .Except(last_bound_cell_list)
                .GroupBy(
                    cell => MapHelper.CalcServerIdFromCell(cell, Program.game_server_num),
                    cell => MapHelper.GetPositionKey(cell)
                )
                .Select(group => new { server_id = group.Key, position_key_list = group.ToList() });

            foreach (var item in object_spawn_list)
            {
                RequestSpawnObjectList(item.server_id, item.position_key_list);
            }
        }

        void RequestSpawnObjectList(int server_id, List<string> position_key_list)
        {
            this.nats_client.Publish(
                $"spawn_object_{server_id}",
                MessagePackSerializer.Serialize(
                    (this.object_info.GetHashField(), position_key_list)
                )
            );
        }

        public void PublishLeave(Cell? leave_cell = null)
        {
            if (leave_cell == null)
            {
                leave_cell = this.last_cell;
            }

            var manage_server = GetObjectManageServer(leave_cell);
            this.nats_client.Publish(
                $"leave_object_{manage_server}",
                MessagePackSerializer.Serialize(
                    (MapHelper.GetPositionKey(leave_cell), this.object_info.GetHashField())
                )
            );
        }

        public void PublishDestroy()
        {
            var manage_server = GetObjectManageServer(this.object_info.current_cell);
            this.nats_client.Publish(
                $"destroy_object_{manage_server}",
                MessagePackSerializer.Serialize(
                    (
                        MapHelper.GetPositionKey(this.object_info.current_cell),
                        this.object_info.GetHashField()
                    )
                )
            );
        }

        public void PublishMove()
        {
            var manage_server = GetObjectManageServer(this.object_info.current_cell);
            this.nats_client.Publish(
                $"move_object_{manage_server}",
                MessagePackSerializer.Serialize(
                    (MapHelper.GetPositionKey(this.last_cell), this.object_info)
                )
            );
        }

#pragma warning disable CS1998

        async Task SubscribeMove(long _, G_TO_U_MOVE body)
        {
            this.move_object_queue.Enqueue(body.object_info);
        }

        async Task SubscribeSpawn(long _, G_TO_U_SPAWN body)
        {
            try
            {
                var object_keys = body.object_key_list;
                var player_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, this.player_id);

                for (int i = 0; i < object_keys.Count; i += Config.BROADCAST_UNIT)
                {
                    List<string> batch = object_keys.Skip(i).Take(Config.BROADCAST_UNIT).ToList();

                    var remain = object_keys.Count - i - Config.BROADCAST_UNIT;
                    var is_ended = remain <= 0;

                    Packet packet = PacketMaker.U_TO_C_SPAWN(
                        batch.Select((item) => item.ToString()).ToList(),
                        is_ended
                    );

                    this.SendToClient(packet);
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
        }

        async Task SubscribeDestroy(long _, G_TO_U_DESTROY body)
        {
            try
            {
                var object_key = body.object_key;

                Packet packet = PacketMaker.U_TO_C_DESTROY(object_key);
                this.SendToClient(packet);

                LogManager.WriteInfoLog(object_key);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
        }

#pragma warning restore CS1998

        public int GetObjectManageServer(Cell cell)
        {
            return MapHelper.CalcServerIdFromCell(cell, Program.game_server_num);
        }
    }
}
