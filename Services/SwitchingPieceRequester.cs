using MonoTorrent;
using MonoTorrent.PiecePicking;

namespace SneakyFlow.Services;

/// <summary>Switches on the engine loop, keeping listeners, DHT and peer connections alive.</summary>
internal sealed class SwitchingPieceRequester(bool sequential) : IPieceRequester
{
    private int _requestedMode = sequential ? 1 : 0, _reset;
    private int _activeMode = sequential ? 1 : 0;
    private IPieceRequester _active = Create(sequential);
    private IPieceRequesterData? _data;
    private IMessageEnqueuer? _enqueuer;
    private ReadOnlyBitField[] _ignored = [];
    private readonly HashSet<IRequester> _peers = [];
    public bool InEndgameMode => _active.InEndgameMode;
    public void SetMode(bool value) => Volatile.Write(ref _requestedMode, value ? 1 : 0);
    public void RequestRecovery() => Interlocked.Exchange(ref _reset, 1);
    private static IPieceRequester Create(bool sequential) => new StandardPieceRequester(
        new PieceRequesterSettings(allowPrioritisation: true, allowRandomised: !sequential, allowRarestFirst: !sequential));
    public void Initialise(IPieceRequesterData data, IMessageEnqueuer enqueuer, ReadOnlySpan<ReadOnlyBitField> ignored)
    {
        _data = data; _enqueuer = enqueuer; _ignored = ignored.ToArray(); _peers.Clear();
        _activeMode = Volatile.Read(ref _requestedMode);
        _active = Create(_activeMode == 1);
        _active.Initialise(data, enqueuer, ignored);
    }
    private void ApplyMode()
    {
        int mode = Volatile.Read(ref _requestedMode);
        int reset = Interlocked.Exchange(ref _reset, 0);
        if (_data is null || (mode == _activeMode && reset == 0)) return;
        // Cancel through the old requester so peer request counters and wire cancellations agree.
        foreach (var peer in _peers) _active.CancelRequests(peer, 0, _data.PieceCount - 1);
        _activeMode = mode;
        _active = Create(mode == 1);
        _active.Initialise(_data, _enqueuer!, _ignored);
    }
    public void AddRequests(ReadOnlySpan<(IRequester Peer, ReadOnlyBitField Available)> peers)
    {
        ApplyMode();
        _peers.Clear();
        foreach (var pair in peers) _peers.Add(pair.Peer);
        _active.AddRequests(peers);
    }
    public void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> peers)
    { ApplyMode(); _peers.Add(peer); _active.AddRequests(peer, available, peers); }
    public bool ValidatePiece(IRequester peer, PieceSegment segment, out bool complete, HashSet<IRequester> involved)
        => _active.ValidatePiece(peer, segment, out complete, involved);
    public bool IsInteresting(IRequester peer, ReadOnlyBitField field) => _active.IsInteresting(peer, field);
    public void CancelRequests(IRequester peer, int start, int end) => _active.CancelRequests(peer, start, end);
    public void RequestRejected(IRequester peer, PieceSegment segment) => _active.RequestRejected(peer, segment);
    public int CurrentRequestCount() => _active.CurrentRequestCount();
}
