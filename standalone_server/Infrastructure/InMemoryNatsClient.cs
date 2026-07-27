using System.Collections.Concurrent;
using network.interfaces;

namespace standalone_server.infrastructure;

public sealed class InMemoryNatsClientFactory : INatsClientFactory
{
    private readonly InMemoryNatsBus _bus = new();

    public void Initialize(string natsEndPoint)
    {
        // The standalone server keeps the same contract but needs no endpoint.
    }

    public INatsClient Create() => new InMemoryNatsClient(_bus);
}

internal sealed class InMemoryNatsBus
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public Guid Subscribe(string subject, Action<string, byte[]> handler)
    {
        var id = Guid.NewGuid();
        _subscriptions[id] = new Subscription(subject, handler);
        return id;
    }

    public void Unsubscribe(Guid id) => _subscriptions.TryRemove(id, out _);

    public void Publish(string subject, byte[] message)
    {
        foreach (Subscription subscription in _subscriptions.Values)
        {
            if (!SubjectMatches(subscription.Subject, subject)) continue;
            subscription.Handler(subject, message.ToArray());
        }
    }

    private static bool SubjectMatches(string pattern, string subject)
    {
        if (pattern == subject) return true;

        string[] patternTokens = pattern.Split('.');
        string[] subjectTokens = subject.Split('.');
        for (int i = 0; i < patternTokens.Length; i++)
        {
            if (patternTokens[i] == ">") return true;
            if (i >= subjectTokens.Length) return false;
            if (patternTokens[i] != "*" && patternTokens[i] != subjectTokens[i]) return false;
        }

        return patternTokens.Length == subjectTokens.Length;
    }

    private sealed record Subscription(string Subject, Action<string, byte[]> Handler);
}

internal sealed class InMemoryNatsClient(InMemoryNatsBus bus) : INatsClient
{
    private readonly List<Guid> _subscriptionIds = [];
    private bool _closed;

    public void Publish(string subject, byte[] message)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        bus.Publish(subject, message);
    }

    public void Subscribe(string subject, Action<string, byte[]> messageHandler)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _subscriptionIds.Add(bus.Subscribe(subject, messageHandler));
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        foreach (Guid id in _subscriptionIds) bus.Unsubscribe(id);
        _subscriptionIds.Clear();
    }
}
