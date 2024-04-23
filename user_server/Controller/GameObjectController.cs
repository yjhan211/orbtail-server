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

        /*-------------------------------------------------------------*/

        public GameObjectInfo object_info { get; private set; }
        Cell last_cell;
        Task move_object_task;
        CancellationTokenSource? move_finish_cts;

        public GameObjectController(GameUser user, GameObjectInfo object_info)
        {
            this.user = user;

            this.move_object_queue = new();

            this.object_info = object_info;
            this.last_cell = Cell.Clone(object_info.current_cell);

            this.move_object_task = Task.Run(RecvMoveObjectTask, user.cts.Token);
        }

        // 다른 객체의 이동 정보 구독. RecvMoveObjectTask에서 일괄 전송
        public void SubscribeMove(GameUser _, G_TO_U_MOVE body)
        {
            // 자기 캐릭터는 PublishMove할때 이미 넣음
            if (
                body.object_info.object_type == ObjectType.PLAYER
                && body.object_info.object_id == this.object_info.object_id
            )
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
        // 어떤 경우에 생기는가: 이미 스폰되어 있는 오브젝트를 만났을 때
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

        public void SubscribeCreateinstanceSuccess(GameUser _, G_TO_U_CREATE_INSTANCE_SUCCESS body)
        {
            if (
                body.map_id == this.object_info.map_id
                && body.map_sub_id == this.object_info.map_sub_id
            )
            {
                var packet = PacketMaker.U_TO_C_CHANGE_MAP(
                    this.object_info.map_id,
                    this.object_info.map_sub_id,
                    this.object_info.current_cell,
                    this.object_info.is_flip
                );

                user.SendToClient(packet);
            }
        }

        // 클라의 이동 요청
        public async Task RequestMove(GameUser _, C_TO_U_MOVE body)
        {
            try
            {
                if (this.user.change_map_task != null)
                {
                    throw new Exception("in change map task");
                }

                var next_target_cell = MapHelper.CalcTargetCell(
                    this.object_info.target_cell,
                    body.direction
                );

                var next_position_key = MapHelper.GetPositionKey(
                    this.object_info.map_id,
                    this.object_info.map_sub_id,
                    next_target_cell
                );

                // 아직 이동이 완료되지 않음
                if (this.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
                {
                    throw new Exception(
                        $"{next_position_key} | {this.object_info.GetMoveElapsedTime()}"
                    );
                }

                int manage_server_id = 0;
                switch (this.object_info.map_id)
                {
                    case MapID.LAB_1:
                        manage_server_id = MapHelper.GetServerIdByMapSubID(
                            Program.game_server_num,
                            this.object_info.map_sub_id
                        );
                        break;

                    default:
                        manage_server_id = MapHelper.GetServerIdByPositionKey(
                            Program.game_server_num,
                            next_position_key
                        );
                        break;
                }

                if (manage_server_id == 0)
                {
                    throw new Exception($"not found server id from manage part");
                }

                PlayerInfo? player_info = await PlayerInfoController.Load(
                    user.cache_helper,
                    user.player_id
                );

                if (player_info == null)
                {
                    throw new Exception($"not found player info");
                }

                var portal_key = MapHelper.GetPortalKey(this.object_info.map_id, next_target_cell);
                var target_cell_update = IsMoveAblePosition(portal_key, player_info);
                if (!target_cell_update)
                {
                    next_target_cell = this.object_info.target_cell;
                }

                await Move(next_target_cell, body.direction, player_info);

                var error_code = target_cell_update
                    ? ErrorCode.SUCCESS
                    : ErrorCode.INVALID_POSITION;

                Packet packet = PacketMaker.U_TO_C_MOVE(
                    user.player_id,
                    error_code,
                    this.object_info
                );

                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                Packet packet = PacketMaker.U_TO_C_MOVE(
                    user.player_id,
                    ErrorCode.FATAL,
                    this.object_info
                );
                user.SendToClient(packet);

                LogManager.WriteErrorLog(e);
            }
            finally
            {
                // this.object_lock.Release();
            }
        }

        public static bool IsMoveAblePosition(string portal_key, PlayerInfo player_info)
        {
            if (MapHelper.portal_info.TryGetValue(portal_key, out var portal_result))
            {
                var map_id = portal_result.Item1;
                switch (map_id)
                {
                    case MapID.LAB_1:
                        if (player_info.lab_id == 0)
                        {
                            // 소속 연구소가 없음
                            return false;
                        }
                        break;

                    default:
                        break;
                }
            }

            return true;
        }

        // 공통맵에서만 쓰고 있어서 일단 냅둠
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
                this.object_info.map_sub_id,
                this.object_info.current_cell
            );

            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                current_position_key
            );

            var subject = MapHelper.GetMoveManageSubject(
                this.object_info.map_id,
                this.object_info.map_sub_id,
                manage_server
            );

            user.nats_client.Publish(
                subject,
                MessagePackSerializer.Serialize((current_position_key, this.object_info))
            );

            this.move_object_queue.Enqueue(this.object_info);
        }

        // 이동
        public async Task Move(
            Cell next_target_cell,
            DirectionType direction,
            PlayerInfo? player_info = null,
            bool all_bound = false
        )
        {
            // 진행중인 MoveFinish 취소
            if (this.move_finish_cts != null && !this.move_finish_cts.IsCancellationRequested)
            {
                this.move_finish_cts.Cancel();
                this.move_finish_cts.Dispose();
            }

            // 과거 위치 챙겨놓고
            this.last_cell = Cell.Clone(this.object_info.current_cell);

            var last_position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.map_sub_id,
                this.object_info.current_cell
            );

            // current_cell을 target_cell로 변경
            this.object_info.current_cell = Cell.Clone(this.object_info.target_cell);

            var current_position_key = MapHelper.GetPositionKey(
                this.object_info.map_id,
                this.object_info.map_sub_id,
                this.object_info.current_cell
            );

            int last_manage_server;
            int current_manage_server;

            switch (this.object_info.map_id)
            {
                case MapID.LAB_1:
                    last_manage_server = MapHelper.GetServerIdByMapSubID(
                        Program.game_server_num,
                        this.object_info.map_sub_id
                    );
                    current_manage_server = last_manage_server;
                    break;

                default:
                    last_manage_server = MapHelper.GetServerIdByPositionKey(
                        Program.game_server_num,
                        last_position_key
                    );
                    current_manage_server = MapHelper.GetServerIdByPositionKey(
                        Program.game_server_num,
                        current_position_key
                    );
                    break;
            }

            // target_cell을 새로운 target_cell로 변경 및 move_timestamp 업데이트
            this.object_info.move_timestamp = DateTime.UtcNow;
            this.object_info.target_cell = Cell.Clone(next_target_cell);
            if (direction != DirectionType.NONE)
            {
                this.object_info.SetFlip(direction);
            }

            await GameObjectInfoController.Save(user.cache_helper, this.object_info);

            // 과거 담당 서버에는 영역을 떠났다고 전송
            if (last_manage_server != current_manage_server)
            {
                user.nats_client.Publish(
                    MapHelper.GetLeaveManageSubject(
                        this.object_info.map_id,
                        this.object_info.map_sub_id,
                        last_manage_server
                    ),
                    MessagePackSerializer.Serialize(
                        (last_position_key, this.object_info.GetHashField())
                    )
                );
            }

            var move_manage_subject = MapHelper.GetMoveManageSubject(
                this.object_info.map_id,
                this.object_info.map_sub_id,
                current_manage_server
            );

            // 현재 담당 서버에 전송
            user.nats_client.Publish(
                move_manage_subject,
                MessagePackSerializer.Serialize((last_position_key, this.object_info))
            );

            this.move_object_queue.Enqueue(this.object_info);

            switch (this.object_info.map_id)
            {
                case MapID.LAB_1:
                    var instance_key = MapHelper.GetInstanceKey(
                        this.object_info.map_id,
                        this.object_info.map_sub_id
                    );
                    RequestSpawnObjectList(current_manage_server, new() { instance_key });
                    break;

                default:
                    RequestCommmonMapSpawnList(all_bound);
                    break;
            }

            // target_cell이 포탈 좌표면 맵 이동 예약
            var portal_key = MapHelper.GetPortalKey(
                this.object_info.map_id,
                this.object_info.target_cell
            );

            if (MapHelper.portal_info.TryGetValue(portal_key, out var portal_result))
            {
                var map_id = portal_result.Item1;
                var spawn_position = portal_result.Item2;
                var is_flip = portal_result.Item3;

                long map_sub_id = 0;
                switch (map_id)
                {
                    case MapID.LAB_1:
                        map_sub_id = player_info!.lab_id;
                        break;

                    default:
                        break;
                }

                this.user.change_map_task = (
                    this.object_info.move_timestamp.AddSeconds(Config.MOVE_ELAPSED_TIME),
                    (map_id, map_sub_id, spawn_position, is_flip)
                );

                return;
            }

            // 마지막 이동 요청이면 MoveFinish로 Move를 한번 더 호출해야 함. current_cell을 target_cell이랑 일치시키기 위함
            if (direction != DirectionType.NONE)
            {
                this.move_finish_cts = new();
                _ = Task.Run(() => MoveFinish(move_finish_cts.Token));
            }
        }

        async Task MoveFinish(CancellationToken ct)
        {
            var delayTask = Task.Delay(TimeSpan.FromSeconds(Config.MOVE_ELAPSED_TIME * 2), ct);

            while (!ct.IsCancellationRequested && !delayTask.IsCompleted)
            {
                await Task.Yield();
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (this.object_info == null)
            {
                return;
            }

            if (this.object_info.current_cell.Equals(this.object_info.target_cell))
            {
                return;
            }

            _ = Move(this.object_info.target_cell, DirectionType.NONE);
        }

        public void RequestCommmonMapSpawnList(bool all_bound)
        {
            var last_bound_cell_list = all_bound
                ? new()
                : MapHelper.GetBoundCellList(this.last_cell);

            var current_bound_cell_list = MapHelper.GetBoundCellList(this.object_info.current_cell);

            // 현재 바운드 - 이전 바운드 = spawn 대상 TODO map_id
            var object_spawn_list = current_bound_cell_list
                .Except(last_bound_cell_list)
                .Select(
                    (last_bound_cell) =>
                        MapHelper.GetPositionKey(this.object_info.map_id, 0, last_bound_cell)
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

        public async Task ChangeMap(MapID map_id, long map_sub_id, Cell spawn_cell, bool is_flip)
        {
            // 기존 맵에 삭제 요청
            await PublishDestroy();

            this.object_info.map_id = map_id;
            this.object_info.map_sub_id = map_sub_id;
            this.object_info.current_cell = Cell.Clone(spawn_cell);
            this.object_info.target_cell = Cell.Clone(spawn_cell);
            this.object_info.is_flip = is_flip;

            switch (map_id)
            {
                case MapID.LAB_1:
                    var server_id = MapHelper.GetServerIdByMapSubID(
                        Program.game_server_num,
                        this.object_info.map_sub_id
                    );
                    user.nats_client.Publish(
                        MapHelper.GetCreateInstanceSubject(server_id),
                        MessagePackSerializer.Serialize(
                            (
                                this.object_info.GetHashField(),
                                this.object_info.map_id,
                                this.object_info.map_sub_id
                            )
                        )
                    );
                    break;

                default:
                    var packet = PacketMaker.U_TO_C_CHANGE_MAP(
                        map_id,
                        map_sub_id,
                        spawn_cell,
                        is_flip
                    );
                    user.SendToClient(packet);
                    break;
            }
        }

        // 최초 맵 입장 or 이동 시 새로운 영역에 대한 오브젝트 정보 요청
        void RequestSpawnObjectList(int server_id, List<string>? position_key_list = null)
        {
            user.nats_client.Publish(
                MapHelper.GetSpawnManageSubject(
                    this.object_info.map_id,
                    this.object_info.map_sub_id,
                    server_id
                ),
                MessagePackSerializer.Serialize(
                    (this.object_info.GetHashField(), position_key_list ?? new())
                )
            );
        }

        // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
        public async Task PublishDestroy()
        {
            await GameObjectInfoController.Save(user.cache_helper, this.object_info);

            string? key;
            int manage_server;

            switch (this.object_info.map_id)
            {
                case MapID.LAB_1:
                    key = MapHelper.GetInstanceKey(
                        this.object_info.map_id,
                        this.object_info.map_sub_id
                    );
                    manage_server = MapHelper.GetServerIdByMapSubID(
                        Program.game_server_num,
                        this.object_info.map_sub_id
                    );
                    break;

                default:
                    key = MapHelper.GetPositionKey(
                        this.object_info.map_id,
                        this.object_info.map_sub_id,
                        this.object_info.current_cell
                    );
                    manage_server = MapHelper.GetServerIdByPositionKey(
                        Program.game_server_num,
                        key
                    );
                    break;
            }

            user.nats_client.Publish(
                MapHelper.GetDestroyObjectSubject(
                    this.object_info.map_id,
                    this.object_info.map_sub_id,
                    manage_server
                ),
                MessagePackSerializer.Serialize((key, this.object_info.GetHashField()))
            );
        }
    }
}
