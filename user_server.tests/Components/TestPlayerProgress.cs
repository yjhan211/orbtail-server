using network.interfaces;
using user_server.players;

namespace user_server.tests.components;

public sealed class TestPlayerProgress(GameUser user) : PlayerProgress(user)
{
    private bool _disposed;
    public bool AddProgressItemCalled { get; private set; } = false;
    public IProgressTrackable? LastProgressItem { get; private set; }

    public override void AddProgressItem(IProgressTrackable item, Func<IProgressTrackable, Task> onComplete)
    {
        AddProgressItemCalled = true;
        LastProgressItem = item;
        // 실제 기능은 구현하지 않음 (테스트 목적만)
    }
    
    public new void Dispose()
    {
        Dispose(true);
    }
    
    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                base.Dispose(); // 기본 Dispose 호출
            }
            
            _disposed = true;
        }
    }
}