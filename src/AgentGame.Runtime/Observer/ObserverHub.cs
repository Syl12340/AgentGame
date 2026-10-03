using System.Threading.Channels;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

public sealed class ObserverResyncException() : Exception("Observer queue filled; register again for a fresh snapshot.");
public sealed record ObserverRegistration(SnapshotMessage Snapshot, ObserverSubscription Subscription);

public sealed class ObserverSubscription : IDisposable
{
    private readonly ObserverHub _hub;
    internal readonly Channel<object> Channel;
    public ChannelReader<object> Reader => Channel.Reader;
    public bool RequiresResync { get; internal set; }
    internal ObserverSubscription(ObserverHub hub, int capacity)
    {
        _hub = hub;
        Channel = System.Threading.Channels.Channel.CreateBounded<object>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, AllowSynchronousContinuations = false });
    }
    public void Dispose() => _hub.Detach(this);
}

/// <summary>Registration and publication share a lock; consumers never block the rule owner.</summary>
public sealed class ObserverHub
{
    private readonly object _gate = new();
    private readonly HashSet<ObserverSubscription> _subscribers = [];
    private SnapshotMessage _snapshot;
    private bool _completed;
    internal ObserverHub(SnapshotMessage snapshot) => _snapshot = VisualState.FromSnapshot(snapshot).Snapshot();
    public SnapshotMessage CurrentSnapshot() { lock (_gate) return Clone(_snapshot); }
    public ObserverRegistration Register(int capacity = 128)
    {
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("Observer run is complete.");
            if (_subscribers.Count >= 32) throw new InvalidOperationException("Observer subscriber limit reached.");
            var subscription = new ObserverSubscription(this, capacity);
            _subscribers.Add(subscription);
            return new(Clone(_snapshot), subscription);
        }
    }
    internal sealed record Prepared(long PreviousSeq, object Envelope, SnapshotMessage Snapshot);
    internal Prepared Prepare(object envelope)
    {
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("Observer run is complete.");
            // Freeze and validate the full reduction BEFORE the authority write.
            object owned = Clone(envelope);
            var next = VisualState.FromSnapshot(_snapshot);
            next.Apply(owned);
            return new(_snapshot.BaseSeq, owned, next.Snapshot());
        }
    }
    internal void Publish(Prepared publication)
    {
        lock (_gate)
        {
            if (_completed || publication.PreviousSeq != _snapshot.BaseSeq)
                throw new InvalidOperationException("Only the single run owner may publish.");
            _snapshot = publication.Snapshot;
            foreach (ObserverSubscription subscriber in _subscribers.ToArray())
                if (!subscriber.Channel.Writer.TryWrite(Clone(publication.Envelope)))
                {
                    subscriber.RequiresResync = true;
                    _subscribers.Remove(subscriber);
                    subscriber.Channel.Writer.TryComplete(new ObserverResyncException());
                }
        }
    }
    internal void Detach(ObserverSubscription subscriber)
    {
        lock (_gate) { _subscribers.Remove(subscriber); subscriber.Channel.Writer.TryComplete(); }
    }
    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var subscriber in _subscribers) subscriber.Channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
    private static object Clone(object value) => ObserverCodec.Parse(ObserverCodec.Encode(value));
    private static SnapshotMessage Clone(SnapshotMessage value) => (SnapshotMessage)Clone((object)value);
}
