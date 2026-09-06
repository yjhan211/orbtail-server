// ReSharper disable All
using System.Collections.Generic;

namespace network.common
{
    /// <summary>
    /// 프로토콜 분류 및 라우팅 헬퍼
    /// </summary>
    public static class ProtocolHelper
    {
        /// <summary>
        /// GameServer가 처리하는 실시간 프로토콜
        /// </summary>
        private static readonly HashSet<Protocol> _gameServerProtocols = new()
        {
            Protocol.C_TO_G_HEART_BEAT,
            Protocol.G_TO_C_HEART_BEAT,
            Protocol.C_TO_G_CONNECT,
            Protocol.G_TO_C_CONNECT_RESULT,
            Protocol.G_TO_C_MATCH_ROSTER,
            Protocol.G_TO_C_OBJECT_INFO,
            Protocol.C_TO_G_MOVE,
            Protocol.G_TO_C_MOVE,
            Protocol.C_TO_G_SUMMON_ORB,
            Protocol.C_TO_G_DESTROY_ORB,
            Protocol.G_TO_C_ERROR,
        };

        /// <summary>
        /// UserServer가 처리하는 상태 관리 프로토콜
        /// </summary>
        private static readonly HashSet<Protocol> _userServerProtocols = new()
        {
            // 인증 및 세션
            Protocol.C_TO_U_HEART_BEAT,
            Protocol.U_TO_C_HEART_BEAT,
            Protocol.C_TO_U_LOGIN,
            Protocol.U_TO_C_LOGIN,

            // 인벤토리
            Protocol.U_TO_C_INVENTORY_ITEM_LIST,
            Protocol.U_TO_C_INVENTORY_UPDATE,
            Protocol.C_TO_U_WEAR_ITEM,
            Protocol.U_TO_C_WEAR_ITEM,
            Protocol.C_TO_U_USE_ITEM,
            Protocol.U_TO_C_USE_ITEM,

            // 플레이어 정보

            // 퀘스트

            // 우편함

            // 매칭
            Protocol.C_TO_U_MATCHING,
            Protocol.U_TO_C_MATCHING,
            Protocol.C_TO_U_MATCHING_CANCEL,
            Protocol.U_TO_C_MATCHING_CANCEL,
            Protocol.U_TO_C_MATCHING_SUCCESS,
            Protocol.U_TO_C_MATCHING_FAILED,

            // 채팅

            // 중복 로그인

            // 에러
            Protocol.U_TO_C_ERROR,
        };

        /// <summary>
        /// 프로토콜이 GameServer로 가야 하는지 확인
        /// </summary>
        public static bool IsGameServerProtocol(Protocol protocol)
        {
            return _gameServerProtocols.Contains(protocol);
        }
    }
}
