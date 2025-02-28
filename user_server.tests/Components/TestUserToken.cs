using System.Net.Sockets;
using network.common;
using network.core;
using network.interfaces;
using network.packets;

namespace user_server.tests.components;

public class TestUserToken : UserToken
{
    // ReSharper disable once CollectionNeverQueried.Local
    private readonly Queue<Packet> _sendingQueue = new();
    public bool PacketWasSent { get; set; }
    
    private IPeer? _peer;
    public Protocol LastSentPacketId { get; private set; }
    
    public Action<IPeer>? OnSetPeer { get; set; }
    
    public TestUserToken()
    {
        // LockDisconnect 이미 부모 클래스에서 초기화됨
        Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }
    
    public IPeer? GetPeer()
    {
        return _peer;
    }
    
    public override void SetPeer(IPeer peer)
    {
        _peer = peer;
        // base.SetPeer(peer); // 기본 구현 호출
    }
    
    public override void Send(Packet packet)
    {
        PacketWasSent = true;
        _sendingQueue.Enqueue(packet);
        LastSentPacketId = (Protocol)packet._protocolId;

        // 실제 Send 메서드 호출하지 않고 모킹
        // base.Send(packet);
    }
}