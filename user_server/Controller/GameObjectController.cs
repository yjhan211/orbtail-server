namespace user_server
{
    using System.Collections.Concurrent;
    using network;
    using game_server;
    using MessagePack;
    using StackExchange.Redis;

    public class GameObjectController
    {
        GameUser user;
        readonly ConcurrentQueue<GameObjectInfo> move_object_queue;
        readonly SemaphoreSlim object_lock;

        /*-------------------------------------------------------------*/

        public GameObjectInfo object_info { get; private set; }
        Cell last_cell;
        Task move_object_task;

        public GameObjectController(GameUser user, GameObjectInfo object_info)
        {
            this.user = user;

            this.move_object_queue = new();
            this.object_lock = new(1);

            this.object_info = object_info;
            this.last_cell = Cell.Clone(object_info.current_cell);

            this.move_object_task = Task.Run(RecvMoveObjectTask, user.cts.Token);
        }

        // 다른 객체의 이동 정보 구독. RecvMoveObjectTask에서 일괄 전송
        public void SubscribeMove(GameUser _, G_TO_U_MOVE body)
        {
            // 자기꺼는 PublishMove할때 이미 넣음
            if (body.object_info.object_id == this.object_info.object_id)
            {
                return;
            }

            this.move_object_queue.Enqueue(body.object_info);
        }

        // 오로지 모아서 보내는 목적으로 SubscribeMove로부터 분리
        async Task RecvMoveObjectTask()
        {
            while (!user.cts.Token.IsCancellationRequested)
            {
                try
                {
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
                        Packet packet = PacketMaker.U_TO_C_MAP_UPDATE(
                            game_object_list,
                            DateTime.UtcNow
                        );

                        user.SendToClient(packet);
                    }

                    await Task.Delay(16);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                    user.OnRemoved();
                    break;
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

        // 이 함수가 호출되는 경우: G_TO_U_SPAWN_LIST의 object_key_list에는 있으나 클라에는 GameObjectInfo가 없을 때
        // 어떤 경우에 생기는가: 이미 접속해서 잠수타고 있는 오브젝트를 만났을 때
        public async Task GetObjectInfo(GameUser _, C_TO_U_OBJECT_INFO body)
        {
            RedisValue[] keys = body.object_key_list.ConvertAll(x => (RedisValue)x).ToArray();
            var object_info_list = await GameObjectInfoController.LoadAll(user.cache_helper, keys);
            foreach (var object_info in object_info_list)
            {
                this.move_object_queue.Enqueue(object_info);
            }
        }

        // 스폰해야 할 오브젝트 정보 구독
        public void SubscribeSpawn(GameUser _, G_TO_U_SPAWN body)
        {
            var object_keys = body.object_key_list;
            var player_key = GameObjectInfo.MakeHashField(
                ObjectType.PLAYER,
                this.object_info.object_id
            );

            for (int i = 0; i < object_keys.Count; i += Config.BROADCAST_UNIT)
            {
                List<string> batch = object_keys.Skip(i).Take(Config.BROADCAST_UNIT).ToList();

                var remain = object_keys.Count - i - Config.BROADCAST_UNIT;
                var is_ended = remain <= 0;

                Packet packet = PacketMaker.U_TO_C_SPAWN(
                    batch.Select((item) => item.ToString()).ToList(),
                    is_ended
                );

                user.SendToClient(packet);
            }
        }

        // 삭제해야 할 오브젝트 정보 구독
        public void SubscribeDestroy(GameUser _, G_TO_U_DESTROY body)
        {
            var object_key = body.object_key;

            Packet packet = PacketMaker.U_TO_C_DESTROY(object_key);
            user.SendToClient(packet);
        }

        // 클라의 이동 요청
        public async Task RequestMove(GameUser _, C_TO_U_MOVE body)
        {
            try
            {
                var next_target_cell = MapHelper.CalcTargetCell(
                    this.object_info.target_cell,
                    body.direction
                );

                var next_position_key = MapHelper.GetPositionKey(
                    this.object_info.map_id,
                    next_target_cell
                );

                // 아직 이동이 완료되지 않음
                if (this.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
                {
                    throw new Exception(
                        $"{next_position_key} | {this.object_info.GetMoveElapsedTime()}"
                    );
                }

                if (
                    MapHelper.GetServerIdByPositionKey(Program.game_server_num, next_position_key)
                    == 0
                )
                {
                    throw new Exception($"not found server id from manage part");
                }

                await Move(next_target_cell, body.direction);

                Packet packet = PacketMaker.U_TO_C_MOVE(user.player_id, ErrorCode.SUCCESS);
                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                Packet packet = PacketMaker.U_TO_C_MOVE(user.player_id, ErrorCode.FATAL);
                user.SendToClient(packet);
            }
            finally
            {
                this.object_lock.Release();
            }
        }

        public async Task SetFlip(DirectionType direction)
        {
            if (direction == DirectionType.NONE)
            {
                return;
            }

            this.object_info.SetFlip(direction);
            await GameObjectInfoController.Save(user.cache_helper, this.object_info);

            var current_position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.current_cell
            );

            PublishMove(current_position_key, current_position_key);
        }

        // 이동
        public async Task Move(
            Cell next_target_cell,
            DirectionType direction,
            bool all_bound = false
        )
        {
            var target_position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.target_cell
            );

            // target_cell이 포탈 좌표면 맵 이동
            if (MapHelper.portal_info.TryGetValue(target_position_key, out var portal_result))
            {
                await ChangeMap(portal_result);
                return;
            }

            // await this.object_lock.WaitAsync();

            // 과거 위치 챙겨놓고
            this.last_cell = Cell.Clone(this.object_info.current_cell);

            var last_position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.current_cell
            );

            var last_manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                last_position_key
            );

            // current_cell을 target_cell로 변경
            this.object_info.current_cell = Cell.Clone(this.object_info.target_cell);

            var current_position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.current_cell
            );

            var current_manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                current_position_key
            );

            // target_cell을 새로운 target_cell로 변경 및 move_timestamp 업데이트
            this.object_info.move_timestamp = DateTime.UtcNow;
            this.object_info.target_cell = Cell.Clone(next_target_cell);
            if (direction != DirectionType.NONE)
            {
                this.object_info.SetFlip(direction);
            }

            await GameObjectInfoController.Save(user.cache_helper, this.object_info);

            // this.object_lock.Release();

            if (last_manage_server != current_manage_server)
            {
                // 과거 담당 서버에는 영역을 떠났다고 전송
                PublishLeave(last_position_key);
            }

            // 현재 담당 서버에 전송 - 같은 서버에 PublishLeave 따로 보내면 순서 뒤바뀔 수 있음
            PublishMove(last_position_key, current_position_key);

            var last_bound_cell_list = all_bound
                ? new()
                : MapHelper.GetBoundCellList(this.last_cell, true);

            var current_bound_cell_list = MapHelper.GetBoundCellList(
                this.object_info.current_cell,
                true
            );

            // 현재 바운드 - 이전 바운드 = spawn 대상 TODO map_id
            var object_spawn_list = current_bound_cell_list
                .Except(last_bound_cell_list)
                .Select(
                    (last_bound_cell) =>
                        MapHelper.GetPositionKey(this.object_info.map_id, last_bound_cell)
                )
                .Select(
                    position_key =>
                        new
                        {
                            server_id = MapHelper.GetServerIdByPositionKey(
                                Program.game_server_num,
                                position_key
                            ),
                            position_key
                        }
                )
                .GroupBy(item => item.server_id, item => item.position_key)
                .Select(group => new { server_id = group.Key, position_key_list = group.ToList() });

            foreach (var item in object_spawn_list)
            {
                RequestSpawnObjectList(item.server_id, item.position_key_list);
            }
        }

        public async Task ChangeMap((MapID map_id, Cell spawn_cell, bool is_flip) change_info)
        {
            // 기존 맵에 삭제 요청
            await PublishDestroy();

            var packet = PacketMaker.U_TO_C_CHANGE_MAP(
                change_info.map_id,
                change_info.spawn_cell,
                change_info.is_flip
            );

            user.SendToClient(packet);

            this.object_info.map_id = change_info.map_id;
            this.object_info.current_cell = Cell.Clone(change_info.spawn_cell);
            this.object_info.target_cell = Cell.Clone(change_info.spawn_cell);
            this.object_info.is_flip = change_info.is_flip;
        }

        // 다른 서버의 할당 영역으로 넘어갈 때, 기존 할당되어있던 서버에 삭제 요청
        public void PublishLeave(string leave_position_key)
        {
            var manage_sever = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                leave_position_key
            );

            user.nats_client.Publish(
                MapHelper.GetLeaveManageSubject(this.object_info.map_id, manage_sever),
                MessagePackSerializer.Serialize(
                    (leave_position_key, this.object_info.GetHashField())
                )
            );
        }

        // object_info 이동 요청
        public void PublishMove(string last_position_key, string current_position_key)
        {
            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                current_position_key
            );

            user.nats_client.Publish(
                MapHelper.GetMoveManageSubject(this.object_info.map_id, manage_server),
                MessagePackSerializer.Serialize((last_position_key, this.object_info))
            );

            this.move_object_queue.Enqueue(this.object_info);
        }

        // 최초 맵 입장 or 이동 시 새로운 영역에 대한 오브젝트 정보 요청
        void RequestSpawnObjectList(int server_id, List<string> position_key_list)
        {
            user.nats_client.Publish(
                MapHelper.GetSpawnManageSubject(this.object_info.map_id, server_id),
                MessagePackSerializer.Serialize(
                    (this.object_info.GetHashField(), position_key_list)
                )
            );
        }

        // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
        public async Task PublishDestroy()
        {
            await GameObjectInfoController.Save(user.cache_helper, this.object_info);

            var position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.current_cell
            );

            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                position_key
            );

            user.nats_client.Publish(
                MapHelper.GetDestroyObjectSubject(this.object_info.map_id, manage_server),
                MessagePackSerializer.Serialize((position_key, this.object_info.GetHashField()))
            );
        }
    }
}
