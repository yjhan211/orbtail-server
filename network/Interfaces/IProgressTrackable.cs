namespace network.interfaces;

public interface IProgressTrackable
{
    DateTime GetEndTime();
}

public class ProgressItem(IProgressTrackable trackable, Func<IProgressTrackable, Task> onComplete)
{
    public IProgressTrackable Trackable { get; } = trackable;
    public Func<IProgressTrackable, Task> OnComplete { get; } = onComplete;
}
