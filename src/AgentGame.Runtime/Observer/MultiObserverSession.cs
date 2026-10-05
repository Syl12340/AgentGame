using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

/// <summary>Stages each view independently. The run owner records Batches before calling Commit.</summary>
public sealed class MultiObserverSession
{
    private readonly object _gate = new();
    private readonly MultiScenario _scenario;
    private readonly Dictionary<string, MultiObserverHub> _hubs = new(StringComparer.Ordinal);
    private Dictionary<string, MultiObserverProjection> _projections = new(StringComparer.Ordinal);
    private long _generation, _tick;
    private EpisodeOutcome? _episode;
    private bool _completed;

    public MultiObserverSession(MultiSnapshot initial, IReadOnlyList<MultiObservation> observations, string runId)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _scenario = initial.Scenario;
        var seats = ValidateObservations(initial, observations);
        _tick = initial.Tick;
        _episode = initial.Episode;
        for (int seat = 0; seat < _scenario.SeatCount; seat++)
            Add(new(initial, seats[seat], $"agent:{seat}"));
        Add(new(initial, null, M2Protocol.ViewSpectator));

        void Add(MultiObserverProjection projection)
        {
            _projections.Add(projection.View, projection);
            _hubs.Add(projection.View, new(new MultiSnapshotMessage { RunId = runId, View = projection.View,
                Tick = initial.Tick, AgentStatus = AgentStatusDto.Waiting, State = projection.State }));
        }
    }

    public string[] Views => [.. _hubs.Keys];
    public MultiObserverHub GetHub(string view) => _hubs.TryGetValue(view, out var hub) ? hub :
        throw new ArgumentException("The requested view is not present in this session.", nameof(view));

    public sealed class PreparedCommit
    {
        internal readonly MultiObserverSession Owner;
        internal readonly long Generation, Tick;
        internal readonly EpisodeOutcome? Episode;
        internal readonly Dictionary<string, MultiObserverProjection>? Projections;
        internal readonly (MultiObserverHub Hub, MultiObserverHub.Prepared Publication)[] Publications;
        private readonly string[] _batches;
        internal PreparedCommit(MultiObserverSession owner, long generation, long tick, EpisodeOutcome? episode,
            Dictionary<string, MultiObserverProjection>? projections,
            (MultiObserverHub, MultiObserverHub.Prepared)[] publications)
        {
            Owner = owner; Generation = generation; Tick = tick; Episode = episode;
            Projections = projections; Publications = publications;
            _batches = publications.Select(item => MultiObserverCodec.Encode(item.Item2.Envelope)).ToArray();
        }
        /// <summary>Owned copies for authority recording. Editing these never edits the pending publication.</summary>
        public object[] Batches => _batches.Select(MultiObserverCodec.Parse).ToArray();
    }

    public PreparedCommit PrepareStep(MultiSnapshot after, IReadOnlyList<MultiObservation> observations, MultiStepResult step)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate)
        {
            EnsureActive();
            if (_episode is not null || after.Tick != _tick + 1 || step.Tick != after.Tick ||
                step.Seat != _tick % _scenario.SeatCount || step.Episode != after.Episode)
                throw new ArgumentException("Step does not describe the next shared seat-turn.");
            var seats = ValidateObservations(after, observations);
            var next = new Dictionary<string, MultiObserverProjection>(StringComparer.Ordinal);
            var publications = new List<(MultiObserverHub, MultiObserverHub.Prepared)>();
            foreach (var (view, committed) in _projections)
            {
                var projection = committed.Clone();
                var parsed = M2Protocol.ParseView(view);
                ObserverPatch patch = projection.Apply(after, parsed.IsSpectator ? null : seats[parsed.Seat!.Value]);
                MultiObserverHub hub = _hubs[view];
                var previous = hub.CurrentSnapshot();
                var batch = new MultiStepBatchMessage { RunId = previous.RunId, View = view,
                    Seq = checked(previous.BaseSeq + 1), Tick = after.Tick, AgentStatus = AgentStatusDto.ActionReceived,
                    Patch = patch, Events = projection.Events(step) };
                publications.Add((hub, hub.Prepare(batch)));
                next.Add(view, projection);
            }
            return new(this, _generation, after.Tick, after.Episode, next, publications.ToArray());
        }
    }

    public PreparedCommit PrepareStatus(string view, AgentStatusDto status)
    {
        lock (_gate)
        {
            EnsureActive();
            var hub = GetHub(view);
            var previous = hub.CurrentSnapshot();
            var envelope = new MultiAgentStatusMessage { RunId = previous.RunId, View = view,
                Seq = checked(previous.BaseSeq + 1), Tick = previous.Tick, AgentStatus = status };
            return new(this, _generation, _tick, _episode, null, [(hub, hub.Prepare(envelope))]);
        }
    }

    public void Commit(PreparedCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            EnsureActive();
            if (!ReferenceEquals(commit.Owner, this) || commit.Generation != _generation)
                throw new InvalidOperationException("Prepared commit is foreign, stale or already published.");
            // Check every boundary before publishing any view.
            foreach (var (hub, publication) in commit.Publications)
                if (hub.CurrentSnapshot().BaseSeq != publication.PreviousSeq)
                    throw new InvalidOperationException("A view was published outside the session owner.");
            foreach (var (hub, publication) in commit.Publications) hub.Publish(publication);
            if (commit.Projections is not null) _projections = commit.Projections;
            _tick = commit.Tick; _episode = commit.Episode;
            _generation++;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            foreach (var hub in _hubs.Values) hub.Complete();
        }
    }

    private MultiObservation[] ValidateObservations(MultiSnapshot snapshot, IReadOnlyList<MultiObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (!ReferenceEquals(snapshot.Scenario, _scenario) || observations.Count != _scenario.SeatCount ||
            snapshot.Positions.Length != _scenario.SeatCount || snapshot.Tick < 0)
            throw new ArgumentException("All current seats and the original scenario are required.");
        var seats = new MultiObservation[_scenario.SeatCount];
        foreach (var observation in observations)
        {
            if (observation is null || (uint)observation.Seat >= (uint)seats.Length || seats[observation.Seat] is not null ||
                observation.Tick != snapshot.Tick || observation.Position != snapshot.Positions[observation.Seat])
                throw new ArgumentException("Observation identity, position or tick disagrees with its snapshot.");
            seats[observation.Seat] = observation;
        }
        return seats;
    }
    private void EnsureActive()
    {
        if (_completed) throw new InvalidOperationException("Observer session is complete.");
    }
}
