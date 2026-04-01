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
            Protocol.G_TO_C_PLAYER_INFO,
            Protocol.C_TO_G_MOVE,
            Protocol.G_TO_C_MOVE,
            Protocol.C_TO_G_ATTACK,
            Protocol.C_TO_G_INTERACT,
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
            Protocol.C_TO_U_SET_NAME,
            Protocol.U_TO_C_SET_NAME,

            // 인벤토리
            Protocol.U_TO_C_INVENTORY_ITEM_LIST,
            Protocol.U_TO_C_INVENTORY_UPDATE,
            Protocol.C_TO_U_WEAR_ITEM,
            Protocol.U_TO_C_WEAR_ITEM,
            Protocol.C_TO_U_USE_ITEM,
            Protocol.U_TO_C_USE_ITEM,

            // 플레이어 정보
            Protocol.C_TO_U_PLAYER_INFO,
            Protocol.U_TO_C_PLAYER_INFO,

            // 퀘스트
            Protocol.C_TO_U_QUEST_INCREASE,
            Protocol.U_TO_C_QUEST_UPDATE,
            Protocol.C_TO_U_QUEST_SUCCESS,
            Protocol.U_TO_C_QUEST_SUCCESS,

            // 우편함
            Protocol.C_TO_U_MAIL_LIST,
            Protocol.U_TO_C_MAIL_LIST,
            Protocol.C_TO_U_MAIL_RECEIVE,
            Protocol.U_TO_C_MAIL_RECEIVE,

            // 매칭
            Protocol.C_TO_U_MATCHING,
            Protocol.U_TO_C_MATCHING,
            Protocol.C_TO_U_MATCHING_CANCEL,
            Protocol.U_TO_C_MATCHING_CANCEL,
            Protocol.U_TO_C_MATCHING_SUCCESS,
            Protocol.U_TO_C_MATCHING_FAILED,

            // 채팅
            Protocol.C_TO_U_CHAT_MSG,
            Protocol.U_TO_C_CHAT_MSG,
            Protocol.C_TO_U_CHAT_LOG,

            // 중복 로그인
            Protocol.U_TO_U_DUPLICATE,

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

        /// <summary>
        /// 프로토콜이 UserServer로 가야 하는지 확인
        /// </summary>
        public static bool IsUserServerProtocol(Protocol protocol)
        {
            return _userServerProtocols.Contains(protocol);
        }

        /// <summary>
        /// 프로토콜이 실시간 처리가 필요한지 확인
        /// </summary>
        public static bool IsRealtimeProtocol(Protocol protocol)
        {
            return protocol switch
            {
                Protocol.C_TO_G_MOVE => true,
                Protocol.G_TO_C_MOVE => true,
                Protocol.C_TO_G_ATTACK => true,
                Protocol.C_TO_G_INTERACT => true,
                _ => false
            };
        }

        /// <summary>
        /// 클라이언트가 보내는 프로토콜인지 확인
        /// </summary>
        public static bool IsClientToServerProtocol(Protocol protocol)
        {
            var protocolName = protocol.ToString();
            return protocolName.StartsWith("C_TO_U_") || protocolName.StartsWith("C_TO_G_");
        }

        /// <summary>
        /// 서버가 보내는 프로토콜인지 확인
        /// </summary>
        public static bool IsServerToClientProtocol(Protocol protocol)
        {
            var protocolName = protocol.ToString();
            return protocolName.StartsWith("U_TO_C_") || protocolName.StartsWith("G_TO_C_");
        }
    }
}
