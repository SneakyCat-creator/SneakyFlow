using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;
using MonoTorrent.Trackers;
using MonoTorrent.Dht;
using SneakyFlow.Models;

namespace SneakyFlow.Services;

/// <summary>All mutating methods and Refresh are called from the UI context. Engine events are marshalled through a queue.</summary>
public sealed class TorrentService : IAsyncDisposable
{
    private sealed class Entry(TorrentManager manager, TorrentItem item, string source)
    {
        public TorrentManager Manager { get; } = manager;
        public TorrentItem Item { get; } = item;
        public string Source { get; } = source;
        public bool DesiredRunning { get; set; }
        public bool SequentialPrepared { get; set; }
        public SwitchingPieceRequester? Picker;
        public StallRecovery Recovery { get; } = new();
        public bool MetadataValidated { get; set; }
        public Dictionary<string, FileDownloadPriority> OriginalPriorities { get; set; } = [];
        public DateTimeOffset LastDataAt = DateTimeOffset.UtcNow;
        public DateTimeOffset LastProbeAt, LastScrapeAt, LastRetryAt;
        public long LastDataBytes;
        public List<PeerId> Connected = [];
        public Dictionary<PeerId, long> PeerBytes = [];
        public ConcurrentQueue<TorrentEventItem> PendingEvents { get; } = new();
        public ConcurrentDictionary<string, DateTimeOffset> AnnouncedAt { get; } = new();
        public string LastConnectionFailure = "";
        public int FailedConnections;
        public Task? DiscoveryTask;
        public long PreviousUploaded;
        public string? Failure;
        public bool Removing;
        public CancellationTokenSource DiscoveryCancellation { get; set; } = new();
        public long SampledAt = Stopwatch.GetTimestamp(), SampledReceived, SampledSent, SampledProtocol;
        public DateTimeOffset LastMetricsAt;
        public Dictionary<PeerId, long> PeerSent = [];
        public Queue<(double Seconds, long Received, long Sent, long Protocol)> RateSamples = new();
    }

    private sealed record SavedTorrent
    {
        public string Source { get; set; } = "";
        public string SavePath { get; set; } = "";
        public bool IsPaused { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public long Uploaded { get; set; }
        public bool IsSequential { get; set; }
        public string? SequentialFilePath { get; set; }
        public Dictionary<string, FileDownloadPriority> Priorities { get; set; } = [];
        public Dictionary<string, FileDownloadPriority> OriginalPriorities { get; set; } = [];
    }
    private sealed record SavedSession
    {
        public int Version { get; set; } = 1;
        public AppSettings Settings { get; set; } = new();
        public List<SavedTorrent> Torrents { get; set; } = [];
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _portableDirectory;
    private ClientEngine? _engine;
    private IDhtEngine? _dht;
    private ShutdownPortForwarder? _portForwarder;
    private DiagnosticLog? _log;
    private Task? _dhtRecovery;
    private DateTimeOffset _lastDhtRecovery = DateTimeOffset.UtcNow;
    private int _dhtRecoveryAttempts;
    private bool _initialized, _disposing, _disposed, _maintenanceRunning;
    private DateTimeOffset _maintenanceStarted;
    private bool _maintenanceWarning;
    private DateTime _lastSaved = DateTime.UtcNow;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public ObservableCollection<TorrentItem> Torrents { get; } = [];
    public AppSettings Settings { get; private set; } = new();
    public string DataDirectory { get; }
    public string DownloadDirectory => ResolvePath(Settings.DownloadDirectory);
    public bool IsInitialized => _initialized;
    public long DownloadSpeed => Torrents.Sum(t => t.DownloadSpeed);
    public long UploadSpeed => Torrents.Sum(t => t.UploadSpeed);
    public event EventHandler<string>? Warning;

    /// <param name="portableDirectory">Optional isolated portable root for testing; defaults beside the real exe.</param>
    public TorrentService(string? portableDirectory = null)
    {
        _portableDirectory = Path.GetFullPath(portableDirectory ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
        DataDirectory = Path.Combine(_portableDirectory, "SneakyFlow.Data");
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (_initialized) return;
            Directory.CreateDirectory(DataDirectory);
            var session = await ReadSessionAsync();
            Settings = ValidateSettings(session.Settings);
            L.SetLanguage(Settings.Language);
            Directory.CreateDirectory(DownloadDirectory);
            if (Settings.AdvancedLogging) _log = new DiagnosticLog(DataDirectory);
            var factories = Factories.Default.WithDhtCreator(() => _dht = new DhtEngine())
                .WithPortForwarderCreator(() => _portForwarder = new ShutdownPortForwarder(Factories.Default.CreatePortForwarder()));
            _engine = new ClientEngine(BuildEngineSettings(Settings), factories);
            _log?.Write("SneakyFlow " + typeof(TorrentService).Assembly.GetName().Version);
            _initialized = true;
            foreach (var saved in session.Torrents)
            {
                try
                {
                    var entry = await CreateEntryAsync(saved.Source, ResolvePath(saved.SavePath));
                    entry.DesiredRunning = !saved.IsPaused;
                    entry.Item.AddedAt = saved.AddedAt;
                    entry.PreviousUploaded = saved.Uploaded;
                    entry.Item.IsSequential = Settings.SequentialByDefault;
                    entry.Item.SequentialFilePath = saved.SequentialFilePath;
                    entry.OriginalPriorities = saved.OriginalPriorities;
                    if (entry.Manager.HasMetadata)
                        foreach (var file in entry.Manager.Files)
                            if (saved.Priorities.TryGetValue(file.Path, out var priority))
                                await entry.Manager.SetFilePriorityAsync(file, ToPriority(priority));
                    if (!entry.Item.IsSequential) await RestorePrioritiesAsync(entry);
                    await ConfigurePickerAsync(entry);
                }
                catch (Exception ex) { Warn(L.F($"Не удалось восстановить торрент: {ex.Message}")); }
            }
            await ReconcileAsync();
            RefreshSnapshots();
        }
        finally { _gate.Release(); }
    }

    public async Task<TorrentItem> AddAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException(L.T("Укажите magnet-ссылку или файл .torrent."));
        await _gate.WaitAsync();
        try
        {
            EnsureReady();
            var entry = await CreateEntryAsync(source.Trim().Trim('"'), DownloadDirectory);
            entry.DesiredRunning = Settings.StartAddedTorrents;
            entry.Item.IsSequential = Settings.SequentialByDefault;
            await ConfigurePickerAsync(entry);
            await SaveCoreAsync();
            await ReconcileAsync();
            RefreshSnapshots();
            return entry.Item;
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync(TorrentItem item)
    {
        await _gate.WaitAsync();
        try
        {
            var entry = GetEntry(item);
            entry.DesiredRunning = false;
            await SaveCoreAsync(); // Save intent before waiting for network shutdown.
            await StopAsync(entry);
            await ReconcileAsync();
            RefreshSnapshots();
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync(TorrentItem item)
    {
        await _gate.WaitAsync();
        try
        {
            var entry = GetEntry(item);
            if (entry.Manager.State == TorrentState.Error) await StopAsync(entry);
            entry.Failure = null;
            entry.DesiredRunning = true;
            await SaveCoreAsync();
            await ReconcileAsync();
            RefreshSnapshots();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retry discovery without discarding healthy peer connections. Tracker minimum intervals still apply.</summary>
    public async Task RetryAsync(TorrentItem item)
    {
        Entry entry;
        Task? discovery;
        await _gate.WaitAsync();
        try
        {
            entry = GetEntry(item);
            if (DateTimeOffset.UtcNow - entry.LastRetryAt < TimeSpan.FromSeconds(15)) return;
            entry.LastRetryAt = DateTimeOffset.UtcNow;
            if (entry.Manager.State == TorrentState.Error) await StopAsync(entry);
            entry.Failure = null;
            entry.DesiredRunning = true;
            await ReconcileAsync();
            await SaveCoreAsync();
            if (entry.Manager.State is TorrentState.Stopped or TorrentState.Paused)
            {
                EnqueueEvent(entry, L.T("Раздача ожидает свободного места в очереди загрузок"));
                RefreshSnapshots();
                return;
            }
            EnqueueEvent(entry, L.T("Запрошен повторный поиск пиров: трекеры, DHT и локальная сеть"));
            ScheduleDhtRecovery(manual: true);
            ScheduleDiscovery(entry, manual: true);
            discovery = entry.DiscoveryTask;
            RefreshSnapshots();
        }
        finally { _gate.Release(); }
        if (discovery is not null) await discovery;
        if (!_disposing && !_disposed) RefreshSnapshots();
    }

    /// <summary>Removes the transfer and cache; optionally removes its exact download paths.</summary>
    public async Task RemoveAsync(TorrentItem item, bool deleteDownloadedFiles = false)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureReady();
            // Repeated clicks must not operate on a manager already disposed by the first click.
            if (!_entries.TryGetValue(item.Id, out var entry) || !ReferenceEquals(entry.Item, item)) return;
            var downloadPaths = deleteDownloadedFiles ? entry.Manager.Files.Select(f => f.FullPath).ToArray() : [];
            if (deleteDownloadedFiles)
            {
                foreach (var path in downloadPaths) DownloadDeletion.ValidatePath(entry.Manager.SavePath, path);
                var shared = _entries.Values.Where(e => !ReferenceEquals(e, entry)).SelectMany(e => e.Manager.Files)
                    .Select(f => Path.GetFullPath(f.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (downloadPaths.Any(p => shared.Contains(Path.GetFullPath(p))))
                    throw new IOException(L.T("Эти файлы используются другой раздачей. Сначала удалите только раздачу."));
            }
            entry.Removing = true;
            entry.DesiredRunning = false;
            entry.DiscoveryCancellation.Cancel();
            item.Status = L.T("Удаляется…");
            item.Diagnosis = L.T("Закрываем раздачу…");
            try
            {
                await SaveCoreAsync(); // Persist removal before waiting for remote tracker/drive cleanup.
                await StopAsync(entry);
                // Cache deletion can fail AFTER MonoTorrent unregisters/disposes the manager.
                // Keep it separate so an inaccessible cache cannot leave a dead row in our queue.
                var cachePaths = new[] { entry.Manager.MetadataPath, _engine!.Settings.GetFastResumePath(entry.Manager.InfoHashes) };
                await _engine.RemoveAsync(entry.Manager, RemoveMode.KeepAllData);
                _entries.Remove(item.Id);
                Torrents.Remove(item);
                entry.DiscoveryCancellation.Dispose();
                var savePath = entry.Manager.SavePath;
                await Task.Run(() =>
                {
                    foreach (var path in cachePaths)
                        try { if (File.Exists(path)) File.Delete(path); }
                        catch (IOException ex) { _log?.Write(L.T("Очистка кэша: ") + ex.Message); }
                        catch (UnauthorizedAccessException ex) { _log?.Write(L.T("Очистка кэша: ") + ex.Message); }
                });
                if (deleteDownloadedFiles)
                {
                    var errors = await Task.Run(() => DownloadDeletion.DeleteFiles(savePath, downloadPaths));
                    if (errors.Count > 0) throw new IOException(L.T("Раздача удалена, но некоторые файлы удалить не удалось: ") + string.Join("\n", errors));
                }
                _log?.Write(L.F($"Раздача удалена: {item.Name}"));
                await ReconcileAsync();
            }
            catch
            {
                if (_entries.ContainsKey(item.Id))
                {
                    entry.Removing = false;
                    entry.DiscoveryCancellation.Dispose();
                    entry.DiscoveryCancellation = new();
                    await SaveCoreAsync();
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        var validated = ValidateSettings(settings);
        await _gate.WaitAsync();
        try
        {
            EnsureReady();
            Directory.CreateDirectory(ResolvePath(validated.DownloadDirectory));
            var previous = Settings;
            bool networkChanged = previous.MaxDownloadKiB != validated.MaxDownloadKiB || previous.MaxUploadKiB != validated.MaxUploadKiB ||
                previous.MaxConnections != validated.MaxConnections || previous.ListenPort != validated.ListenPort ||
                previous.EnableDht != validated.EnableDht || previous.EnableLocalPeerDiscovery != validated.EnableLocalPeerDiscovery ||
                previous.EnablePortForwarding != validated.EnablePortForwarding;
            if (networkChanged)
            {
                var updated = new EngineSettingsBuilder(BuildEngineSettings(validated));
                // Preserve endpoint identity when only a rate/connection limit changes.
                if (previous.EnableDht == validated.EnableDht && previous.ListenPort == validated.ListenPort)
                    updated.DhtEndPoint = _engine!.Settings.DhtEndPoint;
                if (previous.ListenPort == validated.ListenPort)
                    updated.ListenEndPoints = _engine!.Settings.ListenEndPoints.ToDictionary(p => p.Key, p => p.Value);
                await _engine!.UpdateSettingsAsync(updated.ToSettings());
            }
            foreach (var entry in _entries.Values)
            {
                if (previous.EnableDht != validated.EnableDht || previous.MaxConnections != validated.MaxConnections)
                    await entry.Manager.UpdateSettingsAsync(new TorrentSettingsBuilder(entry.Manager.Settings) { AllowDht = validated.EnableDht, AllowPeerExchange = true, MaximumConnections = validated.MaxConnections }.ToSettings());
                if (entry.Item.IsSequential == validated.SequentialByDefault) continue;
                await RestorePrioritiesAsync(entry);
                entry.Item.IsSequential = validated.SequentialByDefault;
                entry.Item.SequentialFilePath = null;
                entry.SequentialPrepared = false;
                await ConfigurePickerAsync(entry);
            }
            Settings = validated;
            if (previous.AdvancedLogging != validated.AdvancedLogging)
            {
                var oldLog = _log;
                _log = validated.AdvancedLogging ? new DiagnosticLog(DataDirectory) : null;
                if (oldLog is not null) await oldLog.DisposeAsync();
                foreach (var entry in _entries.Values)
                {
                    entry.PendingEvents.Clear(); entry.AnnouncedAt.Clear();
                    entry.Item.Events.Clear(); entry.Item.PeerDetails.Clear(); entry.Item.Trackers.Clear();
                }
            }
            L.SetLanguage(Settings.Language);
            await ReconcileAsync();
            await SaveCoreAsync();
            RefreshSnapshots();
        }
        finally { _gate.Release(); }
    }

    public async Task SetFilePriorityAsync(TorrentItem item, TorrentFileItem file, FileDownloadPriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        await _gate.WaitAsync();
        try
        {
            var entry = GetEntry(item);
            var engineFile = GetFile(entry, file);
            await entry.Manager.SetFilePriorityAsync(engineFile, ToPriority(priority));
            if (item.IsSequential) entry.OriginalPriorities[file.RelativePath] = priority;
            RefreshSnapshots();
            await SaveCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task SetSequentialAsync(TorrentItem item, bool enabled, TorrentFileItem? file = null)
    {
        await _gate.WaitAsync();
        try
        {
            var entry = GetEntry(item);
            if (file is not null) _ = GetFile(entry, file);
            await RestorePrioritiesAsync(entry);
            item.IsSequential = enabled;
            item.SequentialFilePath = enabled ? file?.RelativePath : null;
            entry.SequentialPrepared = false;
            await ConfigurePickerAsync(entry);
            await ReconcileAsync();
            RefreshSnapshots();
            await SaveCoreAsync();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Refreshes observable statistics and schedules queue/persistence maintenance without blocking the UI.</summary>
    public void Refresh()
    {
        if (!_initialized || _disposing || _disposed) return;
        RefreshSnapshots();
        if (_maintenanceRunning && !_maintenanceWarning && DateTimeOffset.UtcNow - _maintenanceStarted > TimeSpan.FromSeconds(15))
        {
            _maintenanceWarning = true;
            Warn(L.T("Движок долго отвечает на запрос состояния. Интерфейс доступен; повторные операции не запускаются до завершения текущей."));
        }
        if (!_maintenanceRunning) _ = MaintainAsync();
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try { EnsureReady(); await SaveCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task SaveDetailsHeightAsync(double height)
    {
        if (!double.IsFinite(height)) return;
        await _gate.WaitAsync();
        try { EnsureReady(); Settings = Settings with { DetailsPaneHeight = Math.Clamp(height, 240, 3000) }; await SaveCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<Entry> CreateEntryAsync(string source, string downloadDirectory)
    {
        EnsureReady();
        Torrent? torrent = null;
        MagnetLink? magnet = null;
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) magnet = MagnetLink.Parse(source);
        else
        {
            source = ResolvePath(source);
            if (!Path.GetExtension(source).Equals(".torrent", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(L.T("Поддерживаются файлы .torrent и magnet-ссылки."));
            if (new FileInfo(source).Length > 64 * 1024 * 1024) throw new ArgumentException(L.T("Файл метаданных слишком большой (более 64 МБ)."));
            torrent = await Torrent.LoadAsync(source);
            ValidateTorrent(torrent);
        }
        var hashes = torrent?.InfoHashes ?? magnet!.InfoHashes;
        var id = (hashes.V1 ?? hashes.V2!).ToHex();
        if (_entries.ContainsKey(id) || _engine!.Contains(hashes)) throw new InvalidOperationException(L.T("Этот торрент уже добавлен."));
        Directory.CreateDirectory(downloadDirectory);
        var managerSettings = new TorrentSettingsBuilder { AllowDht = Settings.EnableDht, AllowPeerExchange = true, MaximumConnections = Settings.MaxConnections }.ToSettings();
        var manager = magnet is not null
            ? await _engine!.AddAsync(magnet, downloadDirectory, managerSettings)
            : await _engine!.AddAsync(source, downloadDirectory, managerSettings);
        try
        {
            if (manager.HasMetadata) ValidateManagerPaths(manager);
            var item = new TorrentItem { Id = id, Name = string.IsNullOrWhiteSpace(manager.Name) ? id : manager.Name, SavePath = downloadDirectory };
            var entry = new Entry(manager, item, magnet is null ? PortablePath(manager.MetadataPath) : source);
            AttachDiagnostics(entry);
            if (magnet is not null && manager.Torrent?.IsPrivate != true)
            {
                // MonoTorrent 3.0.2 ignores BEP 9 x.pe parameters. Preserve these
                // explicit bootstrap peers, including trackerless/local magnets.
                var peers = ParseExplicitPeers(source).ToArray();
                if (peers.Length > 0)
                {
                    await manager.AddPeersAsync(peers);
                    EnqueueEvent(entry, L.F($"Magnet: добавлены прямые адреса пиров ({peers.Length})"));
                }
            }
            _entries.Add(id, entry);
            Torrents.Add(item);
            return entry;
        }
        catch { await _engine!.RemoveAsync(manager, RemoveMode.CacheDataOnly); throw; }
    }

    private async Task ConfigurePickerAsync(Entry entry)
    {
        if (entry.Picker is null)
        {
            entry.Picker = new SwitchingPieceRequester(entry.Item.IsSequential);
            await entry.Manager.ChangePickerAsync(entry.Picker);
        }
        else entry.Picker.SetMode(entry.Item.IsSequential);
        await PrepareSequentialFileAsync(entry);
    }

    private async Task PrepareSequentialFileAsync(Entry entry)
    {
        if (!entry.Item.IsSequential || entry.SequentialPrepared || !entry.Manager.HasMetadata) return;
        var files = entry.Manager.Files;
        var selected = files.FirstOrDefault(f => f.Path == entry.Item.SequentialFilePath)
            ?? files.Where(f => f.Priority != Priority.DoNotDownload && IsVideoPath(f.Path)).OrderByDescending(f => f.Length).FirstOrDefault()
            ?? files.FirstOrDefault(f => f.Priority != Priority.DoNotDownload);
        if (selected is not null)
        {
            if (entry.OriginalPriorities.Count == 0)
                entry.OriginalPriorities = files.ToDictionary(f => f.Path, f => FromPriority(f.Priority));
            entry.Item.SequentialFilePath = selected.Path;
            await entry.Manager.SetFilePriorityAsync(selected, Priority.Highest);
        }
        entry.SequentialPrepared = true;
    }

    private async Task RestorePrioritiesAsync(Entry entry)
    {
        foreach (var file in entry.Manager.Files)
            if (entry.OriginalPriorities.TryGetValue(file.Path, out var priority))
                await entry.Manager.SetFilePriorityAsync(file, ToPriority(priority));
        entry.OriginalPriorities.Clear();
    }

    private async Task ReconcileAsync()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Manager.HasMetadata && !entry.MetadataValidated)
            {
                try { ValidateManagerPaths(entry.Manager); entry.MetadataValidated = true; }
                catch (Exception ex) { entry.Failure = ex.Message; entry.DesiredRunning = false; await StopAsync(entry); }
            }
            await PrepareSequentialFileAsync(entry);
        }
        var candidates = _entries.Values.Where(e => e.DesiredRunning && e.Failure is null && e.Manager.State != TorrentState.Error)
            .OrderBy(e => e.Item.AddedAt).ToArray();
        var downloads = candidates.Where(e => !SelectedComplete(e.Manager)).Take(Settings.MaxConcurrentDownloads).ToHashSet();
        foreach (var entry in _entries.Values)
        {
            bool shouldRun = entry.DesiredRunning && entry.Failure is null && entry.Manager.State != TorrentState.Error && (SelectedComplete(entry.Manager) || downloads.Contains(entry));
            if (shouldRun)
            {
                if (entry.Manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.HashingPaused) await entry.Manager.StartAsync();
            }
            else if (entry.Manager.State is not (TorrentState.Stopped or TorrentState.Error)) await StopAsync(entry);
        }
    }

    private async Task MaintainAsync()
    {
        _maintenanceRunning = true;
        _maintenanceStarted = DateTimeOffset.UtcNow; _maintenanceWarning = false;
        await _gate.WaitAsync();
        try
        {
            if (_disposing || _disposed) return;
            await ReconcileAsync();
            ScheduleDhtRecovery();
            foreach (var entry in _entries.Values)
            {
                await SampleDiagnosticsAsync(entry);
                if (entry.DesiredRunning && entry.Manager.State is TorrentState.Metadata or TorrentState.Downloading or TorrentState.Seeding)
                    ScheduleDiscovery(entry);
            }
            if (DateTime.UtcNow - _lastSaved > TimeSpan.FromSeconds(30)) await SaveCoreAsync();
            RefreshSnapshots();
        }
        catch (Exception ex) { Warn(L.F($"Не удалось обновить очередь: {ex.Message}")); }
        finally { _gate.Release(); _maintenanceRunning = false; }
    }

    private void AttachDiagnostics(Entry entry)
    {
        // MonoTorrent raises these on worker threads. Only enqueue immutable data here;
        // observable collections are updated on the UI context in RefreshSnapshots.
        entry.Manager.TorrentStateChanged += (_, args) => { if (Settings.AdvancedLogging) EnqueueEvent(entry, L.F($"Состояние: {StateName(args.NewState)}")); };
        entry.Manager.ConnectionAttemptFailed += (_, args) =>
        {
            Interlocked.Increment(ref entry.FailedConnections);
            entry.LastConnectionFailure = args.Reason.ToString();
            if (Settings.AdvancedLogging) EnqueueEvent(entry, L.F($"Не удалось подключиться: {args.Peer.ConnectionUri.Host}:{args.Peer.ConnectionUri.Port} · {args.Reason}"));
        };
        entry.Manager.PeersFound += (_, args) =>
        {
            if (Settings.AdvancedLogging && args.NewPeers > 0) EnqueueEvent(entry, L.F($"Поиск пиров: новых {args.NewPeers}, уже известных {args.ExistingPeers}"));
        };
        entry.Manager.PeerConnected += (_, args) => { if (Settings.AdvancedLogging) EnqueueEvent(entry, L.F($"Пир подключён: {args.Peer.Uri.Host}:{args.Peer.Uri.Port} · {args.Peer.ConnectionDirection}")); };
        entry.Manager.TrackerManager.AnnounceComplete += (_, args) =>
        {
            if (!Settings.AdvancedLogging || args.Tracker is null) return;
            entry.AnnouncedAt[args.Tracker.Uri.AbsoluteUri] = DateTimeOffset.UtcNow;
            EnqueueEvent(entry, args.Successful
                ? L.F($"Трекер {args.Tracker.Uri.Host}: ответ получен, адресов пиров {args.Peers.Values.Sum(p => p.Count)}")
                : L.F($"Трекер {args.Tracker.Uri.Host}: {TrackerFailure(args.Tracker)}"));
        };
    }

    private void EnqueueEvent(Entry entry, string message)
    {
        if (!Settings.AdvancedLogging) return;
        _log?.Write($"[{entry.Item.Name}] {message}");
        entry.PendingEvents.Enqueue(new TorrentEventItem(DateTimeOffset.UtcNow, message));
        while (entry.PendingEvents.Count > 100) entry.PendingEvents.TryDequeue(out _);
    }

    private static string StateName(TorrentState state) => state switch
    {
        TorrentState.Downloading => L.T("загрузка"), TorrentState.Seeding => L.T("раздача"), TorrentState.Metadata => L.T("получение метаданных"),
        TorrentState.Hashing => L.T("проверка файлов"), TorrentState.Starting => L.T("запуск"), TorrentState.Stopping => L.T("остановка"),
        TorrentState.Stopped => L.T("остановлен"), TorrentState.Paused or TorrentState.HashingPaused => L.T("пауза"), TorrentState.Error => L.T("ошибка"), _ => state.ToString()
    };

    private static IEnumerable<PeerInfo> ParseExplicitPeers(string magnet)
    {
        foreach (var parameter in new Uri(magnet).Query.TrimStart('?').Split('&'))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2 || !pair[0].Equals("x.pe", StringComparison.OrdinalIgnoreCase)) continue;
            string address = Uri.UnescapeDataString(pair[1]);
            if (!IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port is < 1 or > 65535) continue;
            var scheme = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            yield return new PeerInfo(new Uri($"{scheme}://{endpoint}"));
        }
    }

    private async Task SampleDiagnosticsAsync(Entry entry)
    {
        if (entry.Removing) return;
        entry.Connected = await entry.Manager.GetPeersAsync();
        var peers = entry.Connected.Where(p => p.IsConnected).ToArray();
        var item = entry.Item;
        item.ConnectedSeeds = peers.Count(p => p.IsSeeder);
        item.ConnectedLeechers = peers.Length - item.ConnectedSeeds;
        var elapsed = Stopwatch.GetElapsedTime(entry.SampledAt).TotalSeconds;
        if (elapsed < 0.25) return;
        entry.SampledAt = Stopwatch.GetTimestamp();
        var monitor = entry.Manager.Monitor;
        long received = monitor.DataBytesReceived, sent = monitor.DataBytesSent, protocol = monitor.ProtocolBytesReceived;
        if (received < entry.SampledReceived || sent < entry.SampledSent) entry.RateSamples.Clear();
        entry.RateSamples.Enqueue((elapsed, Math.Max(0, received - entry.SampledReceived), Math.Max(0, sent - entry.SampledSent), Math.Max(0, protocol - entry.SampledProtocol)));
        while (entry.RateSamples.Count > 4) entry.RateSamples.Dequeue();
        var seconds = entry.RateSamples.Sum(s => s.Seconds);
        item.DownloadSpeed = (long)(entry.RateSamples.Sum(s => s.Received) / seconds);
        item.UploadSpeed = (long)(entry.RateSamples.Sum(s => s.Sent) / seconds);
        item.ProtocolDownloadSpeed = (long)(entry.RateSamples.Sum(s => s.Protocol) / seconds);
        entry.SampledReceived = received; entry.SampledSent = sent; entry.SampledProtocol = protocol;
        item.SessionReceivedBytes = received;
        item.DownloadingPeers = peers.Count(p => entry.PeerBytes.TryGetValue(p, out var before) && p.Monitor.DataBytesReceived > before);
        item.PeerDetails.Clear();
        if (Settings.AdvancedLogging) foreach (var peer in peers.OrderByDescending(p => p.Monitor.DownloadRate))
        {
            var down = entry.PeerBytes.TryGetValue(peer, out var before) ? (long)(Math.Max(0, peer.Monitor.DataBytesReceived - before) / elapsed) : 0;
            var up = entry.PeerSent.TryGetValue(peer, out var beforeUp) ? (long)(Math.Max(0, peer.Monitor.DataBytesSent - beforeUp) / elapsed) : 0;
            item.PeerDetails.Add(new PeerItem(peer.Uri.Authority, peer.ClientApp.ToString(), peer.IsSeeder ? L.T("Сид") : L.T("Пир"),
                !peer.AmInterested ? L.T("Нет нужных частей") : peer.IsChoking ? L.T("Ждём разрешения (choke)") : down > 0 ? L.T("Передаёт данные") : L.T("Ожидаем блоки"),
                down, up, peer.AmRequestingPiecesCount));
        }
        entry.PeerBytes = peers.ToDictionary(p => p, p => p.Monitor.DataBytesReceived);
        entry.PeerSent = peers.ToDictionary(p => p, p => p.Monitor.DataBytesSent);
        item.PendingDiskWriteBytes = _engine!.DiskManager.PendingWriteBytes;
        item.FailedConnectionAttempts = Volatile.Read(ref entry.FailedConnections);
        item.DiskStatus = L.F($"Диск (все раздачи): запись {_engine.DiskManager.WriteRate / 1_000_000d:0.##} МБ/с · ожидает записи {item.PendingDiskWriteBytes / 1_000_000d:0.##} МБ · чтение {_engine.DiskManager.ReadRate / 1_000_000d:0.##} МБ/с");
        item.TransferStatus = L.F($"Получено за сеанс {received / 1_000_000d:0.##} МБ; прогресс учитывает только проверенные сегменты. Служебный трафик: {item.ProtocolDownloadSpeed} Б/с.");
        item.ChokingPeers = peers.Count(p => p.IsChoking);
        item.RequestedBlocks = await entry.Manager.PieceManager.CurrentRequestCountAsync();
        if (entry.Recovery.TryRecover(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - entry.LastDataAt,
            entry.DesiredRunning && entry.Manager.State == TorrentState.Downloading && item.DownloadSpeed == 0 &&
            item.RequestedBlocks == 0 && item.PendingDiskWriteBytes == 0 && peers.Any(p => p.AmInterested && !p.IsChoking)))
        {
            entry.Picker?.RequestRecovery();
            Warn(L.T("Движок перестал запрашивать сегменты. Выполнено ограниченное восстановление запросов без разрыва соединений."));
        }
        if (entry.Manager.HasMetadata && peers.Length > 0)
        {
            var available = new BitField(entry.Manager.Bitfield.Length);
            foreach (var peer in peers)
                if (peer.BitField.Length == available.Length) available.Or(peer.BitField);
            item.AvailablePieces = available.TrueCount;
        }
        else item.AvailablePieces = null;
        if (DateTimeOffset.UtcNow - entry.LastMetricsAt >= TimeSpan.FromSeconds(5))
        {
            entry.LastMetricsAt = DateTimeOffset.UtcNow;
            _log?.Write($"[{item.Name}] payload={item.DownloadSpeed}B/s protocol={item.ProtocolDownloadSpeed}B/s received={received} verified={item.Downloaded} peers={peers.Length} choke={item.ChokingPeers} requests={item.RequestedBlocks} diskPending={item.PendingDiskWriteBytes} DHT={_engine.Dht.State}/{_engine.Dht.NodeCount}");
        }
    }

    private void ScheduleDiscovery(Entry entry, bool manual = false)
    {
        if (entry.Removing || entry.DiscoveryCancellation.IsCancellationRequested) return;
        if (entry.DiscoveryTask is { IsCompleted: false }) return;
        var now = DateTimeOffset.UtcNow;
        bool scrape = now - entry.LastScrapeAt >= TimeSpan.FromMinutes(10);
        bool probe = manual || now - entry.LastProbeAt >= TimeSpan.FromMinutes(2);
        if (!probe && !scrape) return;
        if (probe) entry.LastProbeAt = now;
        if (scrape) entry.LastScrapeAt = now;
        entry.DiscoveryTask = DiscoverAsync(entry, manual, probe, scrape);
    }

    private void ScheduleDhtRecovery(bool manual = false)
    {
        if (!Settings.EnableDht || _dht is null || _dht.Disposed || _dht.State != DhtState.NotReady ||
            _dhtRecovery is { IsCompleted: false } || !_entries.Values.Any(e => e.DesiredRunning && !e.Removing && e.Manager.CanUseDht)) return;
        // MonoTorrent adds a maintenance timer on each StartAsync. Bound automatic retries
        // instead of accumulating timers indefinitely on machines with UDP blocked.
        var interval = manual ? TimeSpan.FromSeconds(15) : _dhtRecoveryAttempts == 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(2);
        if ((!manual && _dhtRecoveryAttempts >= 3) || DateTimeOffset.UtcNow - _lastDhtRecovery < interval) return;
        _lastDhtRecovery = DateTimeOffset.UtcNow;
        _dhtRecoveryAttempts++;
        _dhtRecovery = RecoverDhtAsync(_dht);
    }

    private async Task RecoverDhtAsync(IDhtEngine dht)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var nodes = await DhtBootstrap.ResolveAsync(timeout.Token);
            if (_disposing || !Settings.EnableDht || dht.Disposed || !ReferenceEquals(dht, _dht) ||
                dht.State != DhtState.NotReady || !_entries.Values.Any(e => e.DesiredRunning && !e.Removing && e.Manager.CanUseDht)) return;
            _log?.Write(L.F($"DHT: повторный запуск, адресов резервных узлов {nodes.Length}"));
            dht.Add(nodes);
            await dht.StartAsync();
        }
        catch (OperationCanceledException) { _log?.Write(L.T("DHT: ожидание DNS прервано; повтор доступен через «Обновить соединения»")); }
        catch (Exception ex) { _log?.Write("DHT: " + ex.Message); }
    }

    private async Task DiscoverAsync(Entry entry, bool manual, bool probe, bool scrape)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, entry.DiscoveryCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            if (probe)
            {
                await entry.Manager.DhtAnnounceAsync();
                if (Settings.EnableLocalPeerDiscovery) await entry.Manager.LocalPeerAnnounceAsync();
                if (manual)
                {
                    foreach (var tier in entry.Manager.TrackerManager.Tiers.ToArray())
                    {
                        var tracker = tier.ActiveTracker;
                        var interval = tier.LastAnnounceSucceeded ? tracker.UpdateInterval : tracker.MinUpdateInterval;
                        if (tier.TimeSinceLastAnnounce < interval)
                            EnqueueEvent(entry, L.F($"{tracker.Uri.Host}: повторный запрос разрешён через {Duration(interval - tier.TimeSinceLastAnnounce)}"));
                    }
                }
                // MonoTorrent 3.0.2's specific-tracker overload drops returned peers
                // from AnnounceComplete. The normal path forwards them to the manager,
                // retains tier failover and enforces the tracker's announce intervals.
                await entry.Manager.TrackerManager.AnnounceAsync(timeout.Token);
            }
            if (scrape) await entry.Manager.TrackerManager.ScrapeAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested && !entry.DiscoveryCancellation.IsCancellationRequested) { EnqueueEvent(entry, L.T("Поиск пиров: превышено время ожидания ответа (25 с); автоматические попытки продолжатся")); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EnqueueEvent(entry, L.F($"Поиск пиров: {ex.Message}")); }
    }

    private void RefreshDiagnostics(Entry entry)
    {
        var manager = entry.Manager;
        var item = entry.Item;
        var now = DateTimeOffset.UtcNow;
        if (manager.Monitor.DataBytesReceived != entry.LastDataBytes)
        {
            entry.LastDataBytes = manager.Monitor.DataBytesReceived;
            entry.LastDataAt = now;
        }
        item.KnownPeers = manager.Peers.Available + manager.Peers.Seeds + manager.Peers.Leechs;
        item.HashFailures = manager.HashFails;
        item.Remaining = Math.Max(0, item.Size - item.Downloaded);
        item.Ratio = item.Downloaded == 0 ? 0 : (double)item.Uploaded / item.Downloaded;
        item.ElapsedText = Duration(now - item.AddedAt);
        item.EtaSeconds = item.IsComplete || item.IsPaused || item.DownloadSpeed <= 0 || now - entry.LastDataAt > TimeSpan.FromSeconds(5)
            ? null : Math.Min(TimeSpan.MaxValue.TotalSeconds / 2, (double)item.Remaining / item.DownloadSpeed);
        item.EtaText = item.IsComplete ? L.T("Готово") : item.IsPaused ? L.T("На паузе") : item.EtaSeconds is { } seconds ? Duration(TimeSpan.FromSeconds(seconds)) : "—";
        item.LastActivity = entry.LastDataBytes == 0 ? L.T("Данные ещё не поступали в этом сеансе") : L.F($"Данные получены {Duration(now - entry.LastDataAt)} назад");
        item.ConnectionSummary = L.F($"Подключено {item.Peers}: сидов {item.ConnectedSeeds}, пиров {item.ConnectedLeechers} · известных адресов {item.KnownPeers} · передают {item.DownloadingPeers}");
        item.DhtStatus = !Settings.EnableDht ? L.T("DHT выключен") : !manager.CanUseDht ? L.T("DHT запрещён приватной раздачей") :
            L.F($"DHT: {(_engine!.Dht.State == DhtState.Ready ? L.T("работает") : _engine.Dht.State == DhtState.Initialising ? L.T("подключение к сети") : _dhtRecoveryAttempts >= 3 ? L.T("нет ответа; нажмите «Обновить соединения»") : L.T("ожидается повтор подключения"))} · узлов {_engine!.Dht.NodeCount}");
        item.NetworkStatus = L.F($"TCP IPv4{(Socket.OSSupportsIPv6 ? "/IPv6" : "")}: {(Settings.ListenPort == 0 ? L.T("автопорт") : Settings.ListenPort.ToString())} · UPnP/NAT-PMP: {(Settings.EnablePortForwarding ? L.F($"сопоставлений {_engine!.PortMappings.Created.Count}") : L.T("выключен"))} · лимит подключений {Settings.MaxConnections}");
        RefreshTrackers(entry);
        item.Diagnosis = Diagnose(entry);
        while (entry.PendingEvents.TryDequeue(out var message))
        {
            item.Events.Insert(0, message);
            while (item.Events.Count > 60) item.Events.RemoveAt(item.Events.Count - 1);
        }
    }

    private void RefreshTrackers(Entry entry)
    {
        var item = entry.Item;
        var tiers = entry.Manager.TrackerManager.Tiers.ToArray();
        var trackers = tiers.SelectMany(t => t.Trackers).DistinctBy(t => t.Uri.AbsoluteUri).ToArray();
        if (Settings.AdvancedLogging && !item.Trackers.Select(t => t.Address).SequenceEqual(trackers.Select(t => t.Uri.AbsoluteUri)))
        {
            item.Trackers.Clear();
            foreach (var tracker in trackers) item.Trackers.Add(new TrackerItem { Address = tracker.Uri.AbsoluteUri });
        }
        for (int i = 0; Settings.AdvancedLogging && i < trackers.Length; i++)
        {
            var tracker = trackers[i];
            var model = item.Trackers[i];
            model.Status = tracker.Status switch { TrackerState.Ok => L.T("Работает"), TrackerState.Connecting => L.T("Запрос…"), TrackerState.Offline => L.T("Недоступен"), TrackerState.InvalidResponse => L.T("Ошибка ответа"), _ => L.T("Ожидание") };
            model.Message = !string.IsNullOrWhiteSpace(tracker.FailureMessage) ? tracker.FailureMessage : tracker.WarningMessage ?? "";
            model.LastAnnounce = entry.AnnouncedAt.TryGetValue(tracker.Uri.AbsoluteUri, out var announced) ? announced.ToLocalTime().ToString("HH:mm:ss") : "—";
            var next = (tracker.Status == TrackerState.Ok ? tracker.UpdateInterval : tracker.MinUpdateInterval) - tracker.TimeSinceLastAnnounce;
            model.NextAnnounce = !entry.DesiredRunning ? L.T("Пауза") : next <= TimeSpan.Zero ? L.T("Ожидается") : Duration(next);
        }
        // A peer may appear on many trackers: never sum their swarm populations.
        // These are last reported scrape counts, not guaranteed online/reachable users.
        var reports = tiers.SelectMany(t => t.ScrapeInfo.Where(p => p.Key.Equals(entry.Manager.InfoHashes.V1) || p.Key.Equals(entry.Manager.InfoHashes.V2?.Truncate())).Select(p => p.Value)).ToArray();
        item.TrackerSeeds = reports.Length == 0 ? null : reports.Max(r => r.Complete);
        item.TrackerLeechers = reports.Length == 0 ? null : reports.Max(r => r.Incomplete);
        item.TrackerSummary = trackers.Length == 0 ? L.T("Трекеров нет") : L.F($"Трекеры: отвечают {trackers.Count(t => t.Status == TrackerState.Ok)} из {trackers.Length}");
    }

    private string Diagnose(Entry entry)
    {
        var item = entry.Item;
        var manager = entry.Manager;
        if (item.State == TorrentStatus.Error) return L.F($"Ошибка: {item.ErrorMessage}. Проверьте доступ к папке и свободное место, затем нажмите «Повторить».");
        if (item.IsPaused) return L.T("Загрузка остановлена пользователем. Нажмите «Продолжить».");
        if (item.State == TorrentStatus.Queued) return L.F($"Ожидание очереди: одновременно разрешено загрузок {Settings.MaxConcurrentDownloads}.");
        if (item.State == TorrentStatus.Checking) return L.T("Проверяем существующие файлы на диске. Сетевое скачивание начнётся после проверки.");
        if (item.IsComplete) return L.T("Все выбранные данные проверены и загружены. Ожидаем запросы других участников раздачи.");
        if (manager.HasMetadata && manager.Files.All(f => f.Priority == Priority.DoNotDownload)) return L.T("Все файлы исключены из загрузки. Выберите хотя бы один файл.");
        if (item.PendingDiskWriteBytes >= 4 * 1024 * 1024 && item.DownloadSpeed == 0) return L.T("Очередь записи на диск заполнена: движок ждёт диск. Состояние записи показано в обзоре.");
        if (item.DownloadSpeed > 0 && manager.Monitor.DataBytesReceived > 0 && DateTimeOffset.UtcNow - entry.LastDataAt < TimeSpan.FromSeconds(5)) return item.IsSequential
            ? L.T("Данные поступают. Запрашиваем части по порядку; пропуски зависят от доступности частей у пиров.")
            : L.T("Данные поступают. Части проверяются и записываются на диск.");
        string prefix = !item.HasMetadata ? L.T("Ожидаем метаданные magnet-ссылки. ") : "";
        var trackers = manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).ToArray();
        if (item.Peers == 0)
        {
            if (_engine!.Torrents.Sum(t => t.OpenConnections) >= Settings.MaxConnections) return prefix + L.T("Достигнут общий лимит подключений. Приостановите другую раздачу или увеличьте лимит в настройках.");
            if (item.KnownPeers > 0) return prefix + L.F($"Адреса пиров известны ({item.KnownPeers}), подключённых нет. Участники могут быть недоступны или отклонять соединение.") + (entry.FailedConnections > 0 ? L.F($" Неудачных попыток: {entry.FailedConnections}; последняя причина движка: {entry.LastConnectionFailure}.") : L.T(" Соединения устанавливаются."));
            if (trackers.Length == 0 && (!Settings.EnableDht || !manager.CanUseDht)) return prefix + L.T("Нет источника пиров: у раздачи нет трекеров, DHT недоступен. Нужен рабочий трекер или magnet с адресом пира.");
            if (trackers.Length > 0 && trackers.All(t => t.Status is TrackerState.Offline or TrackerState.InvalidResponse)) return prefix + L.T("Ни один трекер не отвечает корректно. Поиск через DHT продолжается, если он доступен.");
            if (Settings.EnableDht && manager.CanUseDht && _engine!.Dht.NodeCount == 0) return prefix + L.T("Пиров пока нет; DHT ещё не нашёл узлы. Если это длится долго, проверьте интернет, VPN и доступ приложения к UDP в брандмауэре.");
            return prefix + L.T("Трекеры и DHT пока не дали доступных пиров. Число сидов на сайте не гарантирует, что они сейчас доступны.");
        }
        if (!item.HasMetadata) return prefix + L.F($"Подключено {item.Peers}; ожидаем ответ пира, который поддерживает передачу метаданных.");
        if (item.ChokingPeers >= item.Peers) return L.F($"Подключено {item.Peers}, но все пока запрещают передачу (choke). Ожидаем свободного слота у участника.");
        if (!entry.Connected.Any(p => p.IsConnected && p.AmInterested)) return L.T("У подключённых пиров пока нет нужных недостающих частей. Ищем других участников раздачи.");
        if (item.RequestedBlocks > 0) return L.F($"Запрошено блоков: {item.RequestedBlocks}. Ожидаем данные от пиров; без новых данных {Duration(DateTimeOffset.UtcNow - entry.LastDataAt)}.");
        return L.F($"Пиры подключены, но данные пока не поступают. Сидов: {item.ConnectedSeeds}.");
    }

    private static string TrackerFailure(ITracker tracker) => string.IsNullOrWhiteSpace(tracker.FailureMessage) ? tracker.Status.ToString() : tracker.FailureMessage;
    private static string Duration(TimeSpan time) => time.TotalDays >= 1 ? L.F($"{(int)time.TotalDays} д {time.Hours} ч") : time.TotalHours >= 1 ? L.F($"{(int)time.TotalHours} ч {time.Minutes} мин") : time.TotalMinutes >= 1 ? L.F($"{(int)time.TotalMinutes} мин {time.Seconds} с") : L.F($"{Math.Max(0, (int)time.TotalSeconds)} с");

    private void RefreshSnapshots()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Removing) continue;
            var manager = entry.Manager;
            var item = entry.Item;
            item.HasMetadata = manager.HasMetadata;
            item.Name = string.IsNullOrWhiteSpace(manager.Name) ? item.Name : manager.Name;
            item.Progress = manager.PartialProgress;
            item.Size = manager.Torrent?.Size ?? 0;
            if (manager.HasMetadata)
            {
                item.PieceCount = manager.Bitfield.Length;
                var map = new byte[item.PieceCount];
                long verifiedBytes = 0;
                int completed = 0;
                for (int piece = 0; piece < map.Length; piece++)
                {
                    if (!manager.Bitfield[piece]) continue;
                    map[piece] = 1;
                    completed++;
                    verifiedBytes += Math.Max(0, Math.Min(manager.Torrent!.PieceLength, item.Size - (long)piece * manager.Torrent.PieceLength));
                }
                item.CompletedPieces = completed;
                item.Downloaded = verifiedBytes;
                if (!item.PieceMap.AsSpan().SequenceEqual(map)) item.PieceMap = map;
            }
            else { item.PieceCount = 0; item.CompletedPieces = 0; item.Downloaded = 0; }
            item.Uploaded = entry.PreviousUploaded + manager.Monitor.DataBytesSent;
            if (manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
            { item.DownloadSpeed = 0; item.UploadSpeed = 0; entry.RateSamples.Clear(); }
            item.Peers = manager.OpenConnections;
            item.IsPaused = !entry.DesiredRunning;
            item.IsComplete = SelectedComplete(manager);
            item.ErrorMessage = entry.Failure ?? manager.Error?.Exception?.Message ?? "";
            item.State = GetStatus(entry);
            item.Status = item.State switch
            {
                TorrentStatus.Metadata => L.T("Получение метаданных"), TorrentStatus.Downloading => L.T("Загружается"),
                TorrentStatus.Seeding => L.T("Раздаётся"), TorrentStatus.Paused => L.T("На паузе"), TorrentStatus.Checking => L.T("Проверка файлов"),
                TorrentStatus.Error => L.T("Ошибка"), TorrentStatus.Queued => L.T("В очереди"), _ => L.T("Остановлен")
            };
            if (item.Files.Count != manager.Files.Count)
            {
                item.Files.Clear();
                for (int i = 0; i < manager.Files.Count; i++)
                {
                    var f = manager.Files[i];
                    item.Files.Add(new TorrentFileItem { Index = i, Name = Path.GetFileName(f.Path), RelativePath = f.Path, FullPath = f.FullPath, Size = f.Length });
                }
            }
            for (int i = 0; i < manager.Files.Count; i++)
            {
                var file = manager.Files[i];
                var model = item.Files[i];
                model.Progress = file.BitField.PercentComplete;
                model.Priority = FromPriority(file.Priority);
                model.FullPath = file.FullPath;
                model.ContiguousBytes = ContiguousBytes(manager, file);
            }
            if (item.IsSequential)
            {
                var target = item.Files.FirstOrDefault(f => f.RelativePath == item.SequentialFilePath);
                item.StreamingStatus = target is null ? L.T("Последовательный режим · ожидаем метаданные") : L.F($"Последовательно · начало готово на {target.ContiguousProgress:0.0}%");
            }
            else item.StreamingStatus = L.T("Обычная загрузка");
            RefreshDiagnostics(entry);
        }
    }

    private static TorrentStatus GetStatus(Entry entry)
    {
        if (entry.Failure is not null || entry.Manager.State == TorrentState.Error) return TorrentStatus.Error;
        if (!entry.DesiredRunning) return TorrentStatus.Paused;
        return entry.Manager.State switch
        {
            TorrentState.Metadata => TorrentStatus.Metadata,
            TorrentState.Downloading => TorrentStatus.Downloading,
            TorrentState.Seeding => TorrentStatus.Seeding,
            TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.Starting => TorrentStatus.Checking,
            TorrentState.Paused => TorrentStatus.Queued,
            TorrentState.Stopped => TorrentStatus.Queued,
            _ => TorrentStatus.Stopped
        };
    }

    private static bool SelectedComplete(TorrentManager manager) => manager.HasMetadata && manager.Files.Any(f => f.Priority != Priority.DoNotDownload) && manager.PartialProgress >= 100;

    private static long ContiguousBytes(TorrentManager manager, ITorrentManagerFile file)
    {
        if (file.Length == 0 || manager.Torrent is null) return 0;
        int count = 0;
        while (count < file.BitField.Length && file.BitField[count]) count++;
        if (count == file.BitField.Length) return file.Length;
        long fileStart = file.OffsetInTorrent;
        long verifiedEnd = ((long)file.StartPieceIndex + count) * manager.Torrent.PieceLength;
        return Math.Clamp(verifiedEnd - fileStart, 0, file.Length);
    }

    private async Task StopAsync(Entry entry)
    {
        await StopManagerAsync(entry.Manager, TimeSpan.FromSeconds(2));
    }

    private async Task SaveCoreAsync(bool saveResume = true)
    {
        var session = new SavedSession
        {
            Settings = Settings,
            Torrents = _entries.Values.Where(e => !e.Removing).Select(e => new SavedTorrent
            {
                Source = e.Source, SavePath = PortablePath(e.Manager.SavePath), IsPaused = !e.DesiredRunning,
                AddedAt = e.Item.AddedAt, Uploaded = e.PreviousUploaded + e.Manager.Monitor.DataBytesSent,
                IsSequential = e.Item.IsSequential, SequentialFilePath = e.Item.SequentialFilePath,
                Priorities = e.Manager.Files.Where(f => f.Priority != Priority.Normal).ToDictionary(f => f.Path, f => FromPriority(f.Priority)),
                OriginalPriorities = new(e.OriginalPriorities)
            }).ToList()
        };
        var path = Path.Combine(DataDirectory, "session.json");
        var temporary = path + ".tmp";
        await Task.Run(async () =>
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        });
        // Save only the piece bitfields. SaveStateAsync serializes every file path and
        // setting on the engine loop; discarding that huge result stalled large torrents.
        if (saveResume && _engine is not null)
            foreach (var entry in _entries.Values.Where(e => !e.Removing && e.Manager.HasMetadata && e.Manager.HashChecked))
            {
                var resume = await entry.Manager.SaveFastResumeAsync();
                var resumePath = _engine.Settings.GetFastResumePath(entry.Manager.InfoHashes);
                await Task.Run(async () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resumePath)!);
                    var temp = resumePath + ".checkpoint";
                    await File.WriteAllBytesAsync(temp, resume.Encode());
                    File.Move(temp, resumePath, true);
                });
            }
        _lastSaved = DateTime.UtcNow;
    }

    private async Task<SavedSession> ReadSessionAsync()
    {
        var path = Path.Combine(DataDirectory, "session.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<SavedSession>(await File.ReadAllTextAsync(path), JsonOptions) ?? new(); }
        catch (JsonException)
        {
            File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
            Warn(L.T("Файл настроек повреждён. Его резервная копия сохранена в SneakyFlow.Data; используются стандартные настройки."));
            return new();
        }
    }

    private EngineSettings BuildEngineSettings(AppSettings settings) => new EngineSettingsBuilder
    {
        CacheDirectory = Path.Combine(DataDirectory, "Engine"),
        AutoSaveLoadDhtCache = true, AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = true,
        MaximumDownloadRate = checked(settings.MaxDownloadKiB * 1024), MaximumUploadRate = checked(settings.MaxUploadKiB * 1024),
        MaximumConnections = settings.MaxConnections, AllowPortForwarding = settings.EnablePortForwarding,
        MaximumHalfOpenConnections = Math.Min(settings.MaxConnections, 32),
        AllowLocalPeerDiscovery = settings.EnableLocalPeerDiscovery,
        DhtEndPoint = settings.EnableDht ? new IPEndPoint(IPAddress.Any, settings.ListenPort) : null,
        ListenEndPoints = ListenEndpoints(settings.ListenPort),
        UsePartialFiles = false,
        DiskCacheBytes = 0 // Make verified bytes visible promptly to external players opening the actual file.
    }.ToSettings();

    private static Dictionary<string, IPEndPoint> ListenEndpoints(int port)
    {
        var endpoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, port) };
        if (Socket.OSSupportsIPv6) endpoints["ipv6"] = new(IPAddress.IPv6Any, port);
        return endpoints;
    }

    private AppSettings ValidateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.DownloadDirectory)) throw new ArgumentException(L.T("Укажите папку загрузок."));
        _ = ResolvePath(settings.DownloadDirectory);
        if (settings.MaxDownloadKiB is < 0 or > 2097151 || settings.MaxUploadKiB is < 0 or > 2097151) throw new ArgumentException(L.T("Лимит скорости должен быть от 0 до 2147,482624 МБ/с."));
        if (settings.MaxConcurrentDownloads is < 1 or > 100) throw new ArgumentException(L.T("Число одновременных загрузок должно быть от 1 до 100."));
        if (settings.MaxConnections is < 1 or > 5000) throw new ArgumentException(L.T("Число подключений должно быть от 1 до 5000."));
        if (settings.ListenPort is < 0 or > 65535) throw new ArgumentException(L.T("Порт должен быть от 0 до 65535."));
        return settings with { UiScale = double.IsFinite(settings.UiScale) ? Math.Clamp(settings.UiScale, 1, 1.5) : 1, Language = settings.Language == "en" ? "en" : "ru", DownloadDirectory = settings.DownloadDirectory.Trim(),
            DetailsPaneHeight = double.IsFinite(settings.DetailsPaneHeight) ? Math.Clamp(settings.DetailsPaneHeight, 240, 3000) : 340 };
    }

    public static void ValidateTorrent(Torrent torrent)
    {
        foreach (var file in torrent.Files)
        {
            var path = file.Path.Replace('\\', '/');
            if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(part => part is ".." or "." || part.Contains('\0')))
                throw new InvalidDataException(L.T("Торрент содержит небезопасный путь к файлу."));
        }
        if (torrent.Name is "." or ".." || torrent.Name.Contains('/') || torrent.Name.Contains('\\') || torrent.Name.Contains('\0'))
            throw new InvalidDataException(L.T("Торрент содержит небезопасное имя папки."));
    }

    private static void ValidateManagerPaths(TorrentManager manager)
    {
        ValidateTorrent(manager.Torrent!);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manager.SavePath)) + Path.DirectorySeparatorChar;
        foreach (var file in manager.Files)
            if (!Path.GetFullPath(file.FullPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("Путь файла торрента выходит за папку загрузок."));
    }

    private string ResolvePath(string path) => Path.GetFullPath(path, _portableDirectory);
    private string PortablePath(string path)
    {
        var relative = Path.GetRelativePath(_portableDirectory, path);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }
    private Entry GetEntry(TorrentItem item)
    {
        EnsureReady();
        if (!_entries.TryGetValue(item.Id, out var entry) || !ReferenceEquals(entry.Item, item)) throw new InvalidOperationException(L.T("Торрент уже удалён."));
        return entry;
    }
    private static ITorrentManagerFile GetFile(Entry entry, TorrentFileItem file)
    {
        if (file.Index < 0 || file.Index >= entry.Manager.Files.Count || !ReferenceEquals(entry.Item.Files.ElementAtOrDefault(file.Index), file)) throw new ArgumentException(L.T("Файл не принадлежит этому торренту."));
        return entry.Manager.Files[file.Index];
    }
    private static Priority ToPriority(FileDownloadPriority priority) => priority switch { FileDownloadPriority.Skip => Priority.DoNotDownload, FileDownloadPriority.Low => Priority.Low, FileDownloadPriority.High => Priority.High, _ => Priority.Normal };
    private static FileDownloadPriority FromPriority(Priority priority) => priority == Priority.DoNotDownload ? FileDownloadPriority.Skip : priority < Priority.Normal ? FileDownloadPriority.Low : priority > Priority.Normal ? FileDownloadPriority.High : FileDownloadPriority.Normal;
    private static bool IsVideoPath(string path) => new[] { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".ts", ".m2ts", ".wmv", ".mpg", ".mpeg", ".ogv" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    private void EnsureReady() { ThrowIfDisposed(); if (!_initialized) throw new InvalidOperationException(L.T("Сначала инициализируйте торрент-клиент.")); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed || _disposing, this); }
    private void Warn(string message) => Warning?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _disposing) return;
        _disposing = true;
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_initialized)
            {
                try { await SaveCoreAsync(saveResume: false); }
                catch (Exception ex) { Warn(L.F($"Не удалось сохранить сеанс: {ex.Message}")); }
                _portForwarder?.BeginShutdown();
                var results = _entries.Values.Select(async e => { try { await StopManagerAsync(e.Manager, TimeSpan.FromMilliseconds(250)); } catch (Exception ex) { Warn(L.F($"Не удалось корректно остановить «{e.Item.Name}»: {ex.Message}")); } });
                await Task.WhenAll(results);
                try { await SaveCoreAsync(saveResume: false); }
                catch (Exception ex) { Warn(L.F($"Не удалось сохранить сеанс после остановки: {ex.Message}")); }
            }
        }
        finally
        {
            if (_dhtRecovery is not null) await _dhtRecovery;
            if (_engine is not null) await Task.Run(_engine.Dispose);
            _disposed = true; _lifetime.Dispose(); _gate.Release();
            if (_log is not null) await _log.DisposeAsync();
        }
    }

    private static async Task StopManagerAsync(TorrentManager manager, TimeSpan trackerTimeout)
    {
        // StartAsync returns before StartingMode finishes its disk checkpoint. In rev0049
        // that continuation can replace StoppingMode and dispose its token mid-stop.
        // Let the short startup transition settle; retry that specific race at most once.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            while (manager.State == TorrentState.Starting && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
                await Task.Delay(10);
            if (manager.State == TorrentState.Stopped) return;
            try { await manager.StopAsync(trackerTimeout); return; }
            catch (ObjectDisposedException) when (attempt == 0 && manager.State is not (TorrentState.Stopping or TorrentState.Stopped)) { }
        }
    }

}
