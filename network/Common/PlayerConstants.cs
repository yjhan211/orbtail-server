// ReSharper disable All
namespace network.common
{
    /// <summary>
    /// Player 관련 상수 정의
    /// </summary>
    public static class PlayerConstants
    {
        /// <summary>
        /// 더미 플레이어 판별 임계값
        /// PlayerId가 이 값보다 크면 실제 플레이어, 작거나 같으면 더미 플레이어
        /// </summary>
        public const long DUMMY_PLAYER_ID_THRESHOLD = 1000;
    }

}