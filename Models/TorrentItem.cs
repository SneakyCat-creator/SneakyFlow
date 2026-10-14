using System.Collections.ObjectModel;
using System.IO;

namespace SneakyFlow.Models;

public enum TorrentStatus { Queued, Metadata, Downloading, Seeding, Paused, Checking, Error, Stopped }
public enum FileDownloadPriority { Skip, Low, Normal, High }

public sealed class TorrentItem : ObservableModel
{
    private string _name = L.T("Получение метаданных…"), _status = L.T("В очереди"), _errorMessage = "", _streamingStatus = "";
    private TorrentStatus _state;
    private double _progress;
    private long _size, _downloaded, _uploaded, _downloadSpeed, _uploadSpeed;
    private int _peers;
    private bool _isPaused, _hasMetadata, _isComplete, _isSequential;
    private string? _sequentialFilePath;
    private int _connectedSeeds, _connectedLeechers, _knownPeers, _downloadingPeers, _chokingPeers, _pieceCount, _completedPieces, _requestedBlocks, _hashFailures;
    private int? _trackerSeeds, _trackerLeechers, _availablePieces;
    private long _remaining;
    private double _ratio;
    private byte[] _pieceMap = [];
    private string _diagnosis = L.T("Ожидание запуска"), _connectionSummary = "", _dhtStatus = "", _trackerSummary = "", _lastActivity = "", _elapsedText = "", _etaText = "—", _networkStatus = "";

    public string Id { get; internal set; } = "";
    public string InfoHash => Id;
    public string Name { get => _name; internal set => Set(ref _name, value); }
    public string Status { get => _status; internal set { if (Set(ref _status, value)) Notify(nameof(StatusText)); } }
    public string StatusText => Status;
    public TorrentStatus State { get => _state; internal set => Set(ref _state, value); }
    public double Progress { get => _progress; internal set => Set(ref _progress, value); }
    public long Size { get => _size; internal set { if (Set(ref _size, value)) Notify(nameof(SizeBytes)); } }
    public long SizeBytes => Size;
    public long Downloaded { get => _downloaded; internal set { if (Set(ref _downloaded, value)) Notify(nameof(DownloadedBytes)); } }
    public long DownloadedBytes => Downloaded;
    public long Uploaded { get => _uploaded; internal set => Set(ref _uploaded, value); }
    public long DownloadSpeed { get => _downloadSpeed; internal set => Set(ref _downloadSpeed, value); }
    public long UploadSpeed { get => _uploadSpeed; internal set => Set(ref _uploadSpeed, value); }
    public int Peers { get => _peers; internal set => Set(ref _peers, value); }
    public string SavePath { get; internal set; } = "";
    public DateTimeOffset AddedAt { get; internal set; } = DateTimeOffset.Now;
    public bool IsPaused { get => _isPaused; internal set => Set(ref _isPaused, value); }
    public bool HasMetadata { get => _hasMetadata; internal set => Set(ref _hasMetadata, value); }
    public bool IsComplete { get => _isComplete; internal set => Set(ref _isComplete, value); }
    public string ErrorMessage { get => _errorMessage; internal set => Set(ref _errorMessage, value); }
    public bool IsSequential { get => _isSequential; set => Set(ref _isSequential, value); }
    public string StreamingStatus { get => _streamingStatus; set => Set(ref _streamingStatus, value); }
    public string? SequentialFilePath { get => _sequentialFilePath; set => Set(ref _sequentialFilePath, value); }
    public ObservableCollection<TorrentFileItem> Files { get; } = [];
    public int ConnectedSeeds { get => _connectedSeeds; internal set => Set(ref _connectedSeeds, value); }
    public int ConnectedLeechers { get => _connectedLeechers; internal set => Set(ref _connectedLeechers, value); }
    public int KnownPeers { get => _knownPeers; internal set => Set(ref _knownPeers, value); }
    public int DownloadingPeers { get => _downloadingPeers; internal set => Set(ref _downloadingPeers, value); }
    public int ChokingPeers { get => _chokingPeers; internal set => Set(ref _chokingPeers, value); }
    public int? TrackerSeeds { get => _trackerSeeds; internal set => Set(ref _trackerSeeds, value); }
    public int? TrackerLeechers { get => _trackerLeechers; internal set => Set(ref _trackerLeechers, value); }
    public int? AvailablePieces { get => _availablePieces; internal set => Set(ref _availablePieces, value); }
    public int PieceCount { get => _pieceCount; internal set => Set(ref _pieceCount, value); }
    public int CompletedPieces { get => _completedPieces; internal set => Set(ref _completedPieces, value); }
    public int RequestedBlocks { get => _requestedBlocks; internal set => Set(ref _requestedBlocks, value); }
    public int HashFailures { get => _hashFailures; internal set => Set(ref _hashFailures, value); }
    /// <summary>One byte per actual torrent piece: 0 missing, 1 hash-verified. Never an estimated progress bar.</summary>
    public byte[] PieceMap { get => _pieceMap; internal set => Set(ref _pieceMap, value); }
    public long Remaining { get => _remaining; internal set => Set(ref _remaining, value); }
    public double Ratio { get => _ratio; internal set => Set(ref _ratio, value); }
    public string Diagnosis { get => _diagnosis; internal set => Set(ref _diagnosis, value); }
    public string ConnectionSummary { get => _connectionSummary; internal set => Set(ref _connectionSummary, value); }
    public string DhtStatus { get => _dhtStatus; internal set => Set(ref _dhtStatus, value); }
    public string TrackerSummary { get => _trackerSummary; internal set => Set(ref _trackerSummary, value); }
    public string NetworkStatus { get => _networkStatus; internal set => Set(ref _networkStatus, value); }
    public string LastActivity { get => _lastActivity; internal set => Set(ref _lastActivity, value); }
    public string ElapsedText { get => _elapsedText; internal set => Set(ref _elapsedText, value); }
    public string EtaText { get => _etaText; internal set => Set(ref _etaText, value); }
    public double? EtaSeconds { get; internal set; }
    private string _diskStatus = "", _transferStatus = "";
    private long _protocolDownloadSpeed, _sessionReceivedBytes, _pendingDiskWriteBytes;
    private int _failedConnectionAttempts;
    public string DiskStatus { get => _diskStatus; internal set => Set(ref _diskStatus, value); }
    public string TransferStatus { get => _transferStatus; internal set => Set(ref _transferStatus, value); }
    public long ProtocolDownloadSpeed { get => _protocolDownloadSpeed; internal set => Set(ref _protocolDownloadSpeed, value); }
    public long SessionReceivedBytes { get => _sessionReceivedBytes; internal set => Set(ref _sessionReceivedBytes, value); }
    public long PendingDiskWriteBytes { get => _pendingDiskWriteBytes; internal set => Set(ref _pendingDiskWriteBytes, value); }
    public int FailedConnectionAttempts { get => _failedConnectionAttempts; internal set => Set(ref _failedConnectionAttempts, value); }
    public ObservableCollection<PeerItem> PeerDetails { get; } = [];
    public ObservableCollection<TrackerItem> Trackers { get; } = [];
    public ObservableCollection<TorrentEventItem> Events { get; } = [];
}

public sealed class TrackerItem : ObservableModel
{
    private string _status = L.T("Ожидание"), _message = "", _lastAnnounce = "—", _nextAnnounce = "—";
    public string Address { get; init; } = "";
    public string Status { get => _status; internal set => Set(ref _status, value); }
    public string Message { get => _message; internal set => Set(ref _message, value); }
    public string LastAnnounce { get => _lastAnnounce; internal set => Set(ref _lastAnnounce, value); }
    public string NextAnnounce { get => _nextAnnounce; internal set => Set(ref _nextAnnounce, value); }
}

public sealed record TorrentEventItem(DateTimeOffset Time, string OriginalMessage)
{
    public string Message => L.Existing(OriginalMessage);
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");
}

public sealed class TorrentFileItem : ObservableModel
{
    private double _progress;
    private FileDownloadPriority _priority = FileDownloadPriority.Normal;
    private string _fullPath = "";
    private long _contiguousBytes;
    public int Index { get; internal set; }
    public string Name { get; internal set; } = "";
    public string RelativePath { get; internal set; } = "";
    public string FullPath { get => _fullPath; internal set => Set(ref _fullPath, value); }
    public long Size { get; internal set; }
    public long Length => Size;
    public double Progress { get => _progress; internal set => Set(ref _progress, value); }
    public long ContiguousBytes { get => _contiguousBytes; internal set { if (Set(ref _contiguousBytes, value)) Notify(nameof(ContiguousProgress)); } }
    public double ContiguousProgress => Size == 0 ? 100 : 100d * ContiguousBytes / Size;
    public FileDownloadPriority Priority { get => _priority; internal set => Set(ref _priority, value); }
    public bool IsVideo => new[] { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".ts", ".m2ts", ".wmv", ".mpg", ".mpeg", ".ogv" }.Contains(Path.GetExtension(Name).ToLowerInvariant());
}
