namespace network.interfaces
{
    public interface IHandler : IDisposable
    {
        Task Initialize();
        Task Stop();
        Task ProcessAsync(object request);
    }
}
