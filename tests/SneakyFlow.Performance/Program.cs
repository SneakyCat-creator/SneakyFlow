using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using ReusableTasks;
using SneakyFlow.Models;
using SneakyFlow.Services;

internal static class Program
{
    private const int MiB = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly PropertyInfo? PendingLimit = typeof(PeerId).GetProperty("MaxPendingRequests", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? SupportedLimit = typeof(PeerId).GetProperty("MaxSupportedPendingRequests", BindingFlags.Instance | BindingFlags.NonPublic);

    public static int Main(string[] args)
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            if (!args.Contains("--run"))
            {
                Console.WriteLine("Synthetic loopback benchmark; no activity without --run. Run from the repository root.");
                Console.WriteLine("Options: --fixture small|medium|large|all --modes service,cache0,cache32,normal32 --rounds 1 --seeders 2 --seconds 180");
                Console.WriteLine("Writes only a fresh artifacts/perf-runs directory. No public trackers, DHT, local discovery, or user downloads.");
                return 0;
            }
            using var context = new TestContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var run = RunAsync(args);
            _ = run.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Pump();
            run.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            // Keep assertion, timeout and cleanup failures inside Main: no CLR crash dialog.
            Console.Error.WriteLine("BENCHMARK FAILED: " + error);
            return 1;
        }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
    }

    private static string Option(string[] args, string key, string fallback)
    {
        int index = Array.IndexOf(args, key);
        return index < 0 ? fallback : args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("Missing " + key);
    }

    private static async Task RunAsync(string[] args)
    {
        string root = Path.GetFullPath(".");
        if (!File.Exists(Path.Combine(root, "SneakyFlow.csproj"))) throw new InvalidOperationException("Run from the SneakyFlow repository root.");
        string run = Path.Combine(root, "artifacts", "perf-runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(run);
        if (args.Contains("--startup")) { await RunStartupAsync(run); return; }
        var cases = new[] { new FixtureSpec("small", 16 * MiB, 64 * 1024), new FixtureSpec("medium", 64 * MiB, MiB), new FixtureSpec("large", 256 * MiB, 16 * MiB) };
        string selected = Option(args, "--fixture", "all");
        if (selected != "all") cases = cases.Where(x => x.Name == selected).ToArray();
        if (cases.Length == 0) throw new ArgumentException("Unknown fixture.");
        var modes = Option(args, "--modes", "service,cache0,cache32").Split(',');
        if (modes.Any(x => x is not ("service" or "cache0" or "cache32" or "normal32"))) throw new ArgumentException("Unknown mode.");
        int rounds = int.Parse(Option(args, "--rounds", "1"));
        int seedCount = int.Parse(Option(args, "--seeders", "2"));
        int seconds = int.Parse(Option(args, "--seconds", "180"));
        if (rounds is < 1 or > 5 || seedCount is < 1 or > 4 || seconds is < 10 or > 600) throw new ArgumentOutOfRangeException("Keep runs bounded: 1-5 rounds, 1-4 seeders, 10-600 seconds per case.");
        Console.WriteLine("ARTIFACTS " + run);
        await File.WriteAllTextAsync(Path.Combine(run, "environment.json"), JsonSerializer.Serialize(new
        {
            Utc = DateTime.UtcNow, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString(),
            ProcessorCount = Environment.ProcessorCount, Source = "linked current TorrentService.cs; MonoTorrent 3.0.2",
            Network = "loopback only; peers supplied directly after Start; discovery is excluded", Args = args,
            Note = "OS file cache is not cleared; seeder hashing warms source data. Use repeated/reversed case order before treating small differences as causal."
        }, Json));
        var results = new List<CaseResult>();
        foreach (var spec in cases)
        {
            var fixture = await CreateFixtureAsync(run, spec);
            var seeders = new List<ClientEngine>();
            var endpoints = new List<int>();
            try
            {
                for (int i = 0; i < seedCount; i++)
                {
                    int port = ReservePort();
                    var engine = new ClientEngine(EngineSettings(Path.Combine(run, spec.Name, "seed-cache-" + i), port, 32 * MiB));
                    seeders.Add(engine);
                    var manager = await engine.AddAsync(fixture.Torrent, fixture.SeedDirectory, new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings());
                    await manager.StartAsync();
                    await UntilAsync(() => manager.State == TorrentState.Seeding, TimeSpan.FromSeconds(90), "Seeder hash check");
                    endpoints.Add(port);
                }
                for (int round = 0; round < rounds; round++)
                {
                    // Reverse alternate rounds to reduce ordering bias from disk/OS caching.
                    foreach (string mode in round % 2 == 0 ? modes : modes.Reverse())
                    {
                        var result = await RunCaseAsync(run, fixture, mode, round + 1, endpoints, seconds);
                        results.Add(result);
                        await File.WriteAllTextAsync(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(results, Json));
                        Console.WriteLine($"RESULT {spec.Name}/{mode}/{round + 1}: {result.Status}, {result.Seconds:F2}s, {result.AverageMiBps:F2} MiB/s, first data {result.FirstDataSeconds:F3}s, first piece {result.FirstPieceSeconds:F3}s, requests max {result.MaxRequests}, no-data gap {result.MaxNoDataGapSeconds:F3}s, flush/stop {result.StopSeconds:F3}s, SHA256 {result.Sha256Matches}");
                    }
                }
            }
            finally
            {
                foreach (var engine in seeders)
                {
                    try { await engine.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (Exception e) { Console.Error.WriteLine("SEED CLEANUP: " + e.Message); }
                    engine.Dispose();
                }
            }
        }
        if (results.Any(r => r.Status != "complete" || !r.Sha256Matches)) throw new InvalidOperationException("Some benchmark transfers did not complete and validate. See summary.json.");
    }

    private static EngineSettings EngineSettings(string cache, int port, int cacheBytes) => new EngineSettingsBuilder
    {
        CacheDirectory = cache,
        ListenEndPoints = new() { ["ipv4"] = new(IPAddress.Loopback, port) },
        DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
        AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false,
        MaximumConnections = 150, MaximumDownloadRate = 0, MaximumUploadRate = 0,
        DiskCacheBytes = cacheBytes, UsePartialFiles = false
    }.ToSettings();

    private static async Task<CaseResult> RunCaseAsync(string run, Fixture fixture, string mode, int round, List<int> ports, int seconds)
    {
        string directory = Directory.CreateDirectory(Path.Combine(run, fixture.Spec.Name, mode + "-" + round)).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(directory, "download")).FullName;
        TorrentService? service = null;
        ClientEngine? standalone = null;
        var result = new CaseResult { Fixture = fixture.Spec.Name, Mode = mode, Round = round, FileBytes = fixture.Spec.Bytes, PieceBytes = fixture.Spec.PieceBytes, Seeders = ports.Count };
        var samples = new List<Sample>();
        var events = new ConcurrentQueue<PieceEvent>();
        var elapsed = new Stopwatch();
        try
        {
            ClientEngine engine;
            TorrentManager manager;
            if (mode == "service")
            {
                string profile = Path.Combine(directory, "profile");
                string data = Directory.CreateDirectory(Path.Combine(profile, "SneakyFlow.Data")).FullName;
                var settings = new AppSettings
                {
                    DownloadDirectory = destination, EnableDht = false, EnableLocalPeerDiscovery = false,
                    EnablePortForwarding = false, ListenPort = 0, MaxDownloadKiB = 0, MaxUploadKiB = 0,
                    SequentialByDefault = true, StartAddedTorrents = false
                };
                await File.WriteAllTextAsync(Path.Combine(data, "session.json"), JsonSerializer.Serialize(new { Version = 1, Settings = settings, Torrents = Array.Empty<object>() }));
                service = new TorrentService(profile);
                service.Warning += (_, message) => { result.Warnings.Add(message); Console.WriteLine("SERVICE WARNING: " + message); };
                await service.InitializeAsync();
                var item = await service.AddAsync(fixture.TorrentPath);
                engine = (ClientEngine)typeof(TorrentService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                manager = engine.Torrents.Single();
                manager.PieceHashed += (_, e) => events.Enqueue(new(elapsed.Elapsed.TotalSeconds, e.PieceIndex, e.HashPassed));
                elapsed.Start();
                await service.ResumeAsync(item);
            }
            else
            {
                standalone = new ClientEngine(EngineSettings(Path.Combine(directory, "cache"), 0, mode == "cache0" ? 0 : 32 * MiB));
                engine = standalone;
                manager = await engine.AddAsync(fixture.Torrent, destination, new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings());
                await manager.ChangePickerAsync(new StandardPieceRequester(mode == "normal32" ? new PieceRequesterSettings() : new PieceRequesterSettings(allowPrioritisation: true, allowRandomised: false, allowRarestFirst: false)));
                manager.PieceHashed += (_, e) => events.Enqueue(new(elapsed.Elapsed.TotalSeconds, e.PieceIndex, e.HashPassed));
                elapsed.Start();
                await manager.StartAsync();
            }
            result.CacheBytes = engine.Settings.DiskCacheBytes;
            foreach (int port in ports) await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{port}")));
            long previousBytes = 0;
            double lastDataAt = 0;
            double lastReport = -10;
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                service?.Refresh();
                var peers = (await manager.GetPeersAsync()).Where(p => p.IsConnected).ToArray();
                int requests = await manager.PieceManager.CurrentRequestCountAsync();
                double now = elapsed.Elapsed.TotalSeconds;
                long bytes = manager.Monitor.DataBytesReceived;
                if (peers.Length > 0) result.FirstPeerSeconds ??= now;
                if (bytes > 0) result.FirstDataSeconds ??= now;
                if (manager.Bitfield.TrueCount > 0) result.FirstPieceSeconds ??= now;
                if (bytes > previousBytes)
                {
                    if (previousBytes > 0) result.MaxNoDataGapSeconds = Math.Max(result.MaxNoDataGapSeconds, now - lastDataAt);
                    lastDataAt = now;
                    previousBytes = bytes;
                }
                var disk = engine.DiskManager;
                int requestingPeers = peers.Count(p => p.AmRequestingPiecesCount > 0);
                samples.Add(new Sample(now, bytes, manager.Bitfield.TrueCount, manager.Monitor.DownloadRate, peers.Length, peers.Count(p => p.IsChoking), requestingPeers, requests,
                    peers.Sum(p => p.AmRequestingPiecesCount), peers.Sum(p => (int?)PendingLimit?.GetValue(p) ?? 0), peers.Sum(p => (int?)SupportedLimit?.GetValue(p) ?? 0),
                    disk.CacheBytesUsed, disk.PendingWriteBytes, disk.PendingReadBytes, disk.ReadRate, disk.WriteRate, disk.TotalBytesRead, disk.TotalBytesWritten, disk.TotalCacheBytesRead));
                result.MaxRequests = Math.Max(result.MaxRequests, requests);
                result.MaxPeers = Math.Max(result.MaxPeers, peers.Length);
                result.MaxRequestingPeers = Math.Max(result.MaxRequestingPeers, requestingPeers);
                result.MaxPendingWriteBytes = Math.Max(result.MaxPendingWriteBytes, disk.PendingWriteBytes);
                if (now - lastReport >= 5)
                {
                    Console.WriteLine($"SAMPLE {fixture.Spec.Name}/{mode}: {now:F1}s, {bytes / (double)MiB:F1} MiB, {manager.Monitor.DownloadRate / (double)MiB:F2} MiB/s, pieces {manager.Bitfield.TrueCount}/{manager.Bitfield.Length}, peers {peers.Length}, requests {requests}, disk {disk.WriteRate / (double)MiB:F2} MiB/s, pending {disk.PendingWriteBytes}");
                    lastReport = now;
                }
                if (manager.Complete) { result.Status = "complete"; break; }
                if (manager.State == TorrentState.Error) throw new IOException("Torrent error", manager.Error?.Exception);
                await Task.Delay(100);
            }
            result.Seconds = elapsed.Elapsed.TotalSeconds;
            result.NetworkBytes = manager.Monitor.DataBytesReceived;
            result.HashFailures = manager.HashFails;
            result.VerifiedPieces = manager.Bitfield.TrueCount;
            result.AverageMiBps = result.NetworkBytes / (double)MiB / result.Seconds;
            result.DataPhaseMiBps = result.NetworkBytes / (double)MiB / Math.Max(0.001, result.Seconds - (result.FirstDataSeconds ?? 0));
            result.DiskBytesRead = engine.DiskManager.TotalBytesRead;
            result.DiskBytesWritten = engine.DiskManager.TotalBytesWritten;
            result.DiskCacheBytesRead = engine.DiskManager.TotalCacheBytesRead;
            result.P95PendingWriteBytes = samples.Select(s => s.PendingWrites).Order().ElementAt((int)((samples.Count - 1) * 0.95));
            var stop = Stopwatch.StartNew();
            if (service is not null) await service.PauseAsync(service.Torrents.Single()).WaitAsync(TimeSpan.FromSeconds(30));
            else await manager.StopAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(30));
            result.StopSeconds = stop.Elapsed.TotalSeconds;
            if (result.Status == "complete")
            {
                await using var stream = File.OpenRead(manager.Files.Single().FullPath);
                result.Sha256Matches = Convert.ToHexString(await SHA256.HashDataAsync(stream)) == fixture.Sha256;
            }
        }
        catch (Exception error)
        {
            result.Status = "error";
            result.Error = error.ToString();
            result.Seconds = elapsed.Elapsed.TotalSeconds;
            Console.Error.WriteLine($"CASE FAILED {fixture.Spec.Name}/{mode}: {error}");
        }
        finally
        {
            try
            {
                if (service is not null) await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                if (standalone is not null) { await standalone.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(20)); standalone.Dispose(); }
            }
            catch (Exception error) { result.Warnings.Add("Cleanup: " + error.Message); }
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, Json));
            await File.WriteAllTextAsync(Path.Combine(directory, "pieces.json"), JsonSerializer.Serialize(events.ToArray(), Json));
            var header = "seconds,networkBytes,verifiedPieces,reportedBytesPerSecond,peers,chokingPeers,requestingPeers,requests,peerRequests,peerRequestLimit,peerSupportedRequestLimit,cacheBytes,pendingWriteBytes,pendingReadBytes,diskReadBytesPerSecond,diskWriteBytesPerSecond,diskBytesRead,diskBytesWritten,diskCacheBytesRead";
            await File.WriteAllLinesAsync(Path.Combine(directory, "samples.csv"), new[] { header }.Concat(samples.Select(s => s.Csv())));
        }
        return result;
    }

    private static async Task<Fixture> CreateFixtureAsync(string run, FixtureSpec spec)
    {
        string directory = Directory.CreateDirectory(Path.Combine(run, spec.Name, "seed")).FullName;
        string path = Path.Combine(directory, "synthetic.mkv");
        var buffer = new byte[MiB];
        var random = new Random(41277);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, MiB, FileOptions.Asynchronous))
        {
            for (int written = 0; written < spec.Bytes; written += buffer.Length)
            {
                random.NextBytes(buffer);
                hash.AppendData(buffer);
                await stream.WriteAsync(buffer);
            }
        }
        var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = spec.PieceBytes };
        var metadata = await creator.CreateAsync(new TorrentFileSource(path));
        string torrentPath = Path.Combine(run, spec.Name, "synthetic.torrent");
        await File.WriteAllBytesAsync(torrentPath, metadata.Encode());
        Console.WriteLine($"FIXTURE {spec.Name}: {spec.Bytes / MiB} MiB, {spec.PieceBytes / 1024} KiB pieces");
        return new(spec, directory, torrentPath, Torrent.Load(metadata), Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RunStartupAsync(string run)
    {
        var fixture = await CreateFixtureAsync(run, new FixtureSpec("startup", 16 * MiB, 64 * 1024));
        int port = ReservePort();
        using var seeder = new ClientEngine(EngineSettings(Path.Combine(run, "seed-cache"), port, 0));
        var seed = await seeder.AddAsync(fixture.Torrent, fixture.SeedDirectory, new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings());
        await seed.StartAsync();
        await UntilAsync(() => seed.State == TorrentState.Seeding, TimeSpan.FromSeconds(15), "Startup seeder");
        var results = new List<object>();
        foreach (int halfOpen in new[] { 8, 32 })
        {
            var connector = new DelayedUnreachableConnector();
            var factories = Factories.Default.WithPeerConnectionCreator("ipv4", uri => new SocketPeerConnection(uri, connector));
            using var engine = new ClientEngine(new EngineSettingsBuilder(EngineSettings(Path.Combine(run, "cache-" + halfOpen), 0, 0))
                { MaximumHalfOpenConnections = halfOpen }.ToSettings(), factories);
            var manager = await engine.AddAsync(fixture.Torrent, Directory.CreateDirectory(Path.Combine(run, "down-" + halfOpen)).FullName);
            // These addresses are intercepted by the connector; no traffic leaves loopback.
            var peers = Enumerable.Range(0, 96).Select(i => new PeerInfo(new Uri($"ipv4://127.0.0.2:{10000 + i}")))
                .Append(new PeerInfo(new Uri($"ipv4://127.0.0.1:{port}"))).ToArray();
            await manager.AddPeersAsync(peers);
            var timer = Stopwatch.StartNew();
            await manager.StartAsync();
            await UntilAsync(() => manager.Monitor.DataBytesReceived > 0, TimeSpan.FromSeconds(30), "Healthy peer behind stale addresses");
            var firstData = timer.Elapsed.TotalSeconds;
            Console.WriteLine($"STARTUP half-open={halfOpen}, stale=96, first payload={firstData:F3}s, max concurrent attempts={connector.Maximum}");
            results.Add(new { HalfOpen = halfOpen, FirstPayloadSeconds = firstData, connector.Maximum });
            await manager.StopAsync(TimeSpan.FromSeconds(1));
        }
        await seeder.StopAllAsync(TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(Path.Combine(run, "startup.json"), JsonSerializer.Serialize(results, Json));
        Console.WriteLine("STARTUP ARTIFACTS " + run);
    }

    private sealed class DelayedUnreachableConnector : ISocketConnector
    {
        private int _active, _maximum;
        public int Maximum => _maximum;
        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            if (uri.Host != "127.0.0.2") return await new SocketConnector().ConnectAsync(uri, token);
            int active = Interlocked.Increment(ref _active);
            int current;
            do { current = _maximum; } while (active > current && Interlocked.CompareExchange(ref _maximum, active, current) != current);
            try { await Task.Delay(400, token); throw new SocketException((int)SocketError.HostUnreachable); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static async Task UntilAsync(Func<bool> ready, TimeSpan timeout, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException(reason);
            await Task.Delay(100);
        }
    }

    private sealed class TestContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        public void Complete() => queue.CompleteAdding();
        public void Pump() { foreach (var work in queue.GetConsumingEnumerable()) work.Callback(work.State); }
        public void Dispose() => queue.Dispose();
    }

    private sealed record FixtureSpec(string Name, int Bytes, int PieceBytes);
    private sealed record Fixture(FixtureSpec Spec, string SeedDirectory, string TorrentPath, Torrent Torrent, string Sha256);
    private sealed record PieceEvent(double Seconds, int Piece, bool Passed);
    private sealed record Sample(double Seconds, long Bytes, int VerifiedPieces, long Rate, int Peers, int Choking, int RequestingPeers, int Requests, int PeerRequests, int PeerRequestLimit, int PeerSupportedRequestLimit, long CacheBytes, long PendingWrites, long PendingReads, long DiskReadRate, long DiskWriteRate, long DiskRead, long DiskWritten, long CacheRead)
    {
        public string Csv() => FormattableString.Invariant($"{Seconds:F4},{Bytes},{VerifiedPieces},{Rate},{Peers},{Choking},{RequestingPeers},{Requests},{PeerRequests},{PeerRequestLimit},{PeerSupportedRequestLimit},{CacheBytes},{PendingWrites},{PendingReads},{DiskReadRate},{DiskWriteRate},{DiskRead},{DiskWritten},{CacheRead}");
    }
    private sealed class CaseResult
    {
        public string Fixture { get; set; } = "";
        public string Mode { get; set; } = "";
        public int Round { get; set; }
        public int FileBytes { get; set; }
        public int PieceBytes { get; set; }
        public int Seeders { get; set; }
        public int CacheBytes { get; set; }
        public string Status { get; set; } = "timeout";
        public string? Error { get; set; }
        public double Seconds { get; set; }
        public long NetworkBytes { get; set; }
        public int VerifiedPieces { get; set; }
        public int HashFailures { get; set; }
        public double AverageMiBps { get; set; }
        public double DataPhaseMiBps { get; set; }
        public double? FirstPeerSeconds { get; set; }
        public double? FirstDataSeconds { get; set; }
        public double? FirstPieceSeconds { get; set; }
        public double MaxNoDataGapSeconds { get; set; }
        public int MaxRequests { get; set; }
        public int MaxPeers { get; set; }
        public int MaxRequestingPeers { get; set; }
        public long MaxPendingWriteBytes { get; set; }
        public long P95PendingWriteBytes { get; set; }
        public long DiskBytesRead { get; set; }
        public long DiskBytesWritten { get; set; }
        public long DiskCacheBytesRead { get; set; }
        public double StopSeconds { get; set; }
        public bool Sha256Matches { get; set; }
        public List<string> Warnings { get; set; } = [];
    }
}
