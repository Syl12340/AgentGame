using System.Threading.Channels;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

/// <summary>A v2 observer registration carries the reset snapshot plus a per-view subscription.</summary>
public sealed record MultiObserverRegistration(MultiSnapshotMessage Snapshot, MultiObserverSubscription Subscription);

/// <summary>
/// A per-view subscription. The bounded queue carries <em>only</em> observer/2 DTOs; overflow
/// never blocks the publisher (TryWrite only) and detaches the subscriber with a resync mark.
/// </summary>
public sealed class MultiObserverSubscription : IDisposable
{
    private readonly MultiObserverHub _hub;
    internal readonly Channel<object> Channel;
    public ChannelReader<object> Reader => Channel.Reader;
    public bool RequiresResync { get; internal set; }
    internal MultiObserverSubscription(MultiObserverHub hub, int capacity)
    {
        _hub = hub;
        Channel = System.Threading.Channels.Channel.CreateBounded<object>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, AllowSynchronousContinuations = false });
    }
    public void Dispose() => _hub.Detach(this);
}

/// <summary>
/// Single-owner observer/2 hub. Registration and publication share one lock; consumers never
/// block the rule owner. Subscriber limit is 32 per view; each view is served by its own hub,
/// so the bound is enforced per hub. Channels are bounded (1..1024), FullMode is Wait but the
/// publisher only ever TryWrites; a full queue detaches that subscriber (resync + completion
/// with the existing <see cref="ObserverResyncException"/>), while every other queue continues.
/// </summary>
public sealed class MultiObserverHub
{
    private readonly object _gate = new();
    private readonly HashSet<MultiObserverSubscription> _subscribers = [];
    private MultiVisualState _state;
    private bool _completed;
    internal MultiObserverHub(MultiSnapshotMessage snapshot) => _state = MultiVisualState.FromSnapshot(snapshot);
    public MultiSnapshotMessage CurrentSnapshot() { lock (_gate) return _state.Snapshot(); }
    public MultiObserverRegistration Register(int capacity = 128)
    {
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("Multi observer run is complete.");
            if (_subscribers.Count >= 32) throw new InvalidOperationException("Multi observer subscriber limit reached.");
            var subscription = new MultiObserverSubscription(this, capacity);
            _subscribers.Add(subscription);
            return new(_state.Snapshot(), subscription);
        }
    }
    internal sealed record Prepared(long PreviousSeq, object Envelope, MultiSnapshotMessage Snapshot)
    {
        internal required MultiVisualState State { get; init; }
    }
    internal Prepared Prepare(object envelope)
    {
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("Multi observer run is complete.");
            // Freeze the v2 message before validating against a staged reducer.
            object owned = MultiObserverCodec.Parse(MultiObserverCodec.Encode(envelope));
            MultiSnapshotMessage current = _state.Snapshot();
            var staged = MultiVisualState.FromSnapshot(current);
            staged.Apply(owned);
            return new(current.BaseSeq, owned, staged.Snapshot()) { State = staged };
        }
    }
    internal void Publish(Prepared publication)
    {
        lock (_gate)
        {
            long currentSeq = _state.Snapshot().BaseSeq;
            if (_completed || publication.PreviousSeq != currentSeq)
                throw new InvalidOperationException("Only the single run owner may publish.");
            _state = publication.State;
            foreach (MultiObserverSubscription subscriber in _subscribers.ToArray())
                if (!subscriber.Channel.Writer.TryWrite(Clone(publication.Envelope)))
                {
                    subscriber.RequiresResync = true;
                    _subscribers.Remove(subscriber);
                    subscriber.Channel.Writer.TryComplete(new ObserverResyncException());
                }
        }
    }
    internal void Detach(MultiObserverSubscription subscriber)
    {
        lock (_gate) { _subscribers.Remove(subscriber); subscriber.Channel.Writer.TryComplete(); }
    }
    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (MultiObserverSubscription subscriber in _subscribers) subscriber.Channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
    private static object Clone(object value) => MultiObserverCodec.Parse(MultiObserverCodec.Encode(value));
}
