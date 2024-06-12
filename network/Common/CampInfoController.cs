namespace network
{
    using game_server;
    using MessagePack;
    using RedLockNet.SERedis;
    using RedLockNet;

    public static class CampInfoController
    {
        public static void Save(CacheHelper cache_helper, CampInfo camp_info)
        {
            GameObjectInfoController.Save(cache_helper, camp_info.object_info);

            cache_helper.HashSet(
                CampInfo.HASH_KEY,
                camp_info.player_id,
                MessagePackSerializer.Serialize(camp_info)
            );
        }

        public static CampInfo? Load(CacheHelper cache_helper, long player_id)
        {
            var serialized_data = cache_helper.HashGet(CampInfo.HASH_KEY, player_id);

            if (serialized_data.IsNull)
            {
                return null;
            }

            var camp_info = MessagePackSerializer.Deserialize<CampInfo?>(serialized_data);

            if (camp_info == null)
            {
                return null;
            }

            var object_info = GameObjectInfoController.Load(
                cache_helper,
                ObjectType.CAMP,
                player_id
            );

            if (object_info == null)
            {
                return null;
            }

            camp_info.object_info = object_info;

            return camp_info;
        }

        public static void Delete(CacheHelper cache_helper, long player_id)
        {
            cache_helper.HashDelete(CampInfo.HASH_KEY, player_id);
        }
    }
}
