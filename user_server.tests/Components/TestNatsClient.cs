using network.interfaces;

namespace user_server.tests.components;

public class TestNatsClient : INatsClient
{
    public bool PublishCalled { get; private set; }
    public List<(string Subject, byte[] Message)> PublishedMessages { get; }
    public bool SubscribeCalled { get; private set; }
    public bool CloseCalled { get; private set; }
    
    public TestNatsClient()
    {
        PublishedMessages = new List<(string, byte[])>();
        Reset();
    }
    
    public void Reset()
    {
        PublishCalled = false;
        PublishedMessages.Clear();
        SubscribeCalled = false;
        CloseCalled = false;
    }
    
    public void Publish(string subject, byte[] message)
    {
        PublishCalled = true;
        PublishedMessages.Add((subject, message));
    }
    
    public void Subscribe(string subject, Action<string, byte[]> messageHandler)
    {
        SubscribeCalled = true;
        // 필요시 구독 정보도 저장
    }
    
    public void Close()
    {
        CloseCalled = true;
    }
}