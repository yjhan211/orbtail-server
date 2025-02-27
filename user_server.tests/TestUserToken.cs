using System.Net.Sockets;
using network.core;
using network.interfaces;
using network.packets;

namespace user_server.tests;

public class TestUserToken : UserToken
{
    private IPeer _peer;
    public bool PacketWasSent { get; private set; }
    public Packet LastSentPacket { get; private set; }
    private Queue<Packet> _sendingQueue = new Queue<Packet>();
    public Action<IPeer> OnSetPeer { get; set; } // 콜백 추가
    
    public TestUserToken()
    {
        // LockDisconnect 이미 부모 클래스에서 초기화됨
        Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }
    
    public IPeer GetPeer()
    {
        return _peer;
    }
    
    public override void SetPeer(IPeer peer)
    {
        _peer = peer; // 로컬 필드 설정
        // base.SetPeer(peer); // 기본 구현 호출
    }
    
    public override void Send(Packet packet)
    {
        PacketWasSent = true;
        LastSentPacket = packet;
        _sendingQueue.Enqueue(packet);
        
        // 실제 Send 메서드 호출하지 않고 모킹
        // base.Send(packet);
    }
}