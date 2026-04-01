namespace network.packets;

/// <summary>
/// 서버→클라이언트 패킷 생성 팩토리
/// partial class로 도메인별 분리:
///   - PacketMaker.UserServer.cs   : U_TO_C_* (유저서버 → 클라이언트)
///   - PacketMaker.GameCore.cs     : G_TO_C_* 핵심 (접속, 이동, 플레이어 정보)
///   - PacketMaker.Explore.cs      : G_TO_C_* 탐색
///   - PacketMaker.InGameInventory.cs : G_TO_C_* 인게임 아이템
///   - PacketMaker.PlayerStatus.cs : G_TO_C_* 스탯/상태
///   - PacketMaker.ExitProcedure.cs: G_TO_C_* 탈출 절차
///   - PacketMaker.Environment.cs  : G_TO_C_* 환경 (문, 복도)
///   - PacketMaker.PlayerInteract.cs : G_TO_C_* 플레이어 상호작용
/// </summary>
public static partial class PacketMaker;
