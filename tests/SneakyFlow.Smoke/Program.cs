using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Trackers;
using ReusableTasks;
using SneakyFlow.Models;
using SneakyFlow.Services;

internal static class Program
{
    // Exercise the service's documented UI-thread contract with a simple dispatcher.
    public static int Main(string[] args)
    {
        try
        {
            using var context = new TestSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var run = args.FirstOrDefault() == "--dht-check" ? DhtCheck.RunAsync() : args.FirstOrDefault() == "--public-torrent" ? RunPublicTorrentAsync(args) : RunAsync(args);
            _ = run.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Pump();
            run.GetAwaiter().GetResult();
            Console.WriteLine("ALL SERVICE INTEGRATION CHECKS PASSED");
            return 0;
        }
        catch (Exception error)
        {
            // An assertion must never escape Main and create a Windows CLR crash dialog.
            Console.Error.WriteLine($"SMOKE FAILED: {error}");
            return 1;
        }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
    }

    private static async Task RunAsync(string[] args)
    {
        var runDirectory = Path.GetFullPath(args.FirstOrDefault()
            ?? Path.Combine("artifacts", "test-runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        Check(!Directory.Exists(runDirectory) || !Directory.EnumerateFileSystemEntries(runDirectory).Any(), "Use a new empty test directory, never an existing profile or downloads folder.");
        Directory.CreateDirectory(runDirectory);
        Console.WriteLine($"Synthetic fixtures and isolated profile: {runDirectory}");
        await VerifyProfileMigrationAsync(runDirectory);
        await UserExperienceChecks.RunAsync(runDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        var profile = Path.Combine(runDirectory, "profile");
        var destination = Directory.CreateDirectory(Path.Combine(runDirectory, "chosen download folder")).FullName;
        var seedDirectory = Directory.CreateDirectory(Path.Combine(runDirectory, "seed")).FullName;
        var warnings = new List<string>();
        int seedPort = ReservePort();
        await using var tracker = new LocalTracker(seedPort);
        var primary = await CreateFixtureAsync(runDirectory, seedDirectory, "sequential-fixture.mkv", 16 * 1024 * 1024, 42117, tracker.Url, token);
        var idleFixture = await CreateFixtureAsync(runDirectory, seedDirectory, "paused-fixture.bin", 256 * 1024, 33, null, token);
        var magnetFixture = await CreateFixtureAsync(runDirectory, seedDirectory, "magnet-fixture.bin", 4 * 1024 * 1024, 917, null, token);
        var settings = new AppSettings
        {
            DownloadDirectory = destination,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false,
            ListenPort = 0,
            MaxDownloadKiB = 512,
            SequentialByDefault = true,
            StartAddedTorrents = true,
        };
        // Configure isolation before initialization; never contact public discovery services.
        var dataPath = Path.Combine(profile, "SneakyFlow.Data");
        Directory.CreateDirectory(dataPath);
        await File.WriteAllTextAsync(Path.Combine(dataPath, "session.json"), JsonSerializer.Serialize(new { Version = 1, Settings = settings, Torrents = Array.Empty<object>() }), token);
        using var seeder = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(runDirectory, "seed-cache"),
            ListenEndPoints = new() { ["ipv4"] = new(IPAddress.Loopback, seedPort) },
            DhtEndPoint = null,
            AllowLocalPeerDiscovery = false,
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            MaximumUploadRate = 256 * 1024,
            UsePartialFiles = false,
        }.ToSettings());
        var seed = await seeder.AddAsync(primary.Torrent, seedDirectory, new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings());
        var magnetSeed = await seeder.AddAsync(magnetFixture.Torrent, seedDirectory, new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings());
        await seed.StartAsync();
        await magnetSeed.StartAsync();
        await UntilAsync(() => seed.State == TorrentState.Seeding && magnetSeed.State == TorrentState.Seeding, "Local seeders ready", token);

        TorrentService? service = null;
        try
        {
            service = NewService(profile, warnings);
            await service.InitializeAsync();
            Check(Path.GetFullPath(service.DownloadDirectory) == destination, "The chosen existing destination must be used exactly.");
            var main = await service.AddAsync(primary.TorrentPath);
            var manager = Manager(service, main);
            await UntilAsync(() => main.State == TorrentStatus.Downloading && main.TrackerSeeds == 5, "Tracker response and empty downloader", token, service);
            Check(main.IsSequential, "New torrents inherit the global sequential setting.");
            Check(main.Downloaded == 0 && main.Peers == 0, "No data can arrive before the only local peer is announced.");
            Check(main.TrackerSeeds == 5 && main.TrackerLeechers == 7, "Tracker swarm estimates must be exposed separately from connected people.");
            Check(main.ConnectedSeeds == 0 && main.ConnectedLeechers == 0, "Swarm estimates must never be misreported as active connections.");
            Check(!string.IsNullOrWhiteSpace(main.Diagnosis), "A stalled torrent must have a visible diagnosis.");
            Check(main.PieceMap.Length > 0 && main.PieceMap.All(value => value == 0), "Empty data must produce an empty piece map.");
            Console.WriteLine($"PASS no-peer state: {main.Diagnosis}; tracker 5 seeds / 7 peers, actual connections 0");

            await service.ApplySettingsAsync(service.Settings with { SequentialByDefault = false });
            Check(!main.IsSequential, "Disabling sequential mode applies to an existing torrent.");
            var idle = await service.AddAsync(idleFixture.TorrentPath);
            Check(!idle.IsSequential, "New torrents inherit disabled sequential mode.");
            await service.PauseAsync(idle);
            await service.ApplySettingsAsync(service.Settings with { SequentialByDefault = true });
            Check(service.Torrents.All(item => item.IsSequential), "Enabling sequential mode applies to every existing torrent.");
            Check(idle.IsPaused, "A global picker change must retain each torrent's paused intent.");
            Console.WriteLine("PASS global sequential mode for existing and new torrents, paused intent retained");

            var pieceOrder = new ConcurrentQueue<int>();
            manager.PieceHashed += (_, e) => { if (e.HashPassed) pieceOrder.Enqueue(e.PieceIndex); };
            // Keep real HTTP announcements, but control the test clock's interval:
            // the engine otherwise clamps this fixture's interval to several minutes.
            var originalTracker = manager.TrackerManager.Tiers.Single().ActiveTracker;
            await manager.TrackerManager.RemoveTrackerAsync(originalTracker);
            var retryTracker = new RetryTracker(originalTracker);
            await manager.TrackerManager.AddTrackerAsync(retryTracker);
            var retryResponses = new ConcurrentQueue<int>();
            manager.TrackerManager.AnnounceComplete += (_, response) =>
            {
                if (ReferenceEquals(response.Tracker, retryTracker))
                    retryResponses.Enqueue(response.Peers.Values.Sum(peers => peers.Count));
            };
            await manager.TrackerManager.AnnounceAsync(TorrentEvent.Started, token);
            await UntilAsync(() => !retryResponses.IsEmpty, "Initial controlled tracker response", token);
            retryResponses.Clear();
            await manager.TrackerManager.ScrapeAsync(retryTracker, token);
            // Let the engine's rate limiter tick before announcing the only data source.
            await Task.Delay(1250, token);
            tracker.AnnouncePeer = true;
            retryTracker.AllowNextAnnounce = true;
            var transferTime = Stopwatch.StartNew();
            await service.RetryAsync(main);
            await UntilAsync(() => !retryResponses.IsEmpty, "Retry tracker response", token);
            Check(retryResponses.Last() > 0, "Retry must forward the actual tracker peer list to the downloader.");
            Console.WriteLine("PASS retry forwards real tracker peers without restarting the torrent");
            await UntilAsync(() => main.Files.Count == 1 && main.Files[0].ContiguousBytes >= 1024 * 1024, "Contiguous first MiB through the real service", token, service);
            Check(!main.IsComplete && main.Progress < 60, "The beginning must be usable while most of the file is still missing.");
            Check(transferTime.Elapsed > TimeSpan.FromMilliseconds(500), "Transfer limits must prevent an unlimited loopback burst.");
            await UntilAsync(() => main.ConnectedSeeds == 1, "Live connected seeder count", token, service);
            Check(main.Peers >= 1 && main.TrackerSeeds == 5, "Connected seed and tracker estimates remain distinct while downloading.");
            Check(main.PieceMap.Any(value => value == 1) && main.PieceMap.Any(value => value == 0), "Piece map must represent verified and missing pieces during download.");
            Check(main.CompletedPieces > 0 && main.CompletedPieces < main.PieceCount, "Piece totals must correspond to incomplete data.");
            Check(pieceOrder.Take(8).Count() == 8 && pieceOrder.Take(8).All(piece => piece < 16), "Sequential mode must request the beginning, not random pieces.");
            Check(manager.StreamProvider is null, "External-player downloads must not allocate an internal stream provider.");
            var localPath = main.Files[0].FullPath;
            Check(localPath.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Downloaded bytes must stay inside the selected destination.");
            Check(localPath.EndsWith(".mkv", StringComparison.Ordinal), "An incomplete file retains its original extension.");
            await VerifyPrefixAsync(localPath, primary.Bytes, token);
            Console.WriteLine($"PASS external player-style reads before completion: {main.Progress:F2}%; first pieces {string.Join(',', pieceOrder.Take(8))}");

            Check(!service.Settings.AdvancedLogging && main.Events.Count == 0 && main.Trackers.Count == 0 && main.PeerDetails.Count == 0,
                "Advanced details must not be collected by default.");
            var activePeers = (await manager.GetPeersAsync()).Where(p => p.IsConnected).ToArray();
            foreach (bool sequential in new[] { false, true, false, true })
            {
                long beforeSwitch = manager.Monitor.DataBytesReceived;
                var switchTime = Stopwatch.StartNew();
                await service.ApplySettingsAsync(service.Settings with { SequentialByDefault = sequential }).WaitAsync(TimeSpan.FromSeconds(3), token);
                Check(switchTime.Elapsed < TimeSpan.FromSeconds(3), "Live mode switch must be prompt.");
                await UntilAsync(() => manager.Monitor.DataBytesReceived > beforeSwitch + 64 * 1024, "Data continues after live mode switch", token, service);
                Check(activePeers.All(p => p.IsConnected), "Changing mode must preserve the existing peer connections.");
            }
            await service.ApplySettingsAsync(service.Settings with { AdvancedLogging = true }).WaitAsync(TimeSpan.FromSeconds(3), token);
            await UntilAsync(() => main.PeerDetails.Count > 0 && main.Trackers.Count > 0, "Live advanced diagnostics", token, service);
            await service.ApplySettingsAsync(service.Settings with { AdvancedLogging = false }).WaitAsync(TimeSpan.FromSeconds(3), token);
            Check(main.Events.Count == 0 && main.Trackers.Count == 0 && main.PeerDetails.Count == 0, "Disabling details clears retained views.");
            Check(activePeers.All(p => p.IsConnected), "Logging toggle must preserve connections.");
            Console.WriteLine("PASS four live requester switches, uninterrupted peer connections, live logging enable/disable");

            await service.PauseAsync(main);
            Check(main.IsPaused && main.State == TorrentStatus.Paused, "Pause must visibly stop the selected transfer.");
            var pausedBytes = main.Downloaded;
            await Task.Delay(350, token);
            service.Refresh();
            Check(main.Downloaded == pausedBytes, "A paused transfer must not gain verified bytes.");
            await service.SaveAsync();
            await service.DisposeAsync();
            service = NewService(profile, warnings);
            await service.InitializeAsync();
            main = service.Torrents.Single(item => item.Id == main.Id);
            idle = service.Torrents.Single(item => item.Id == idle.Id);
            Check(service.Torrents.Count == 2 && main.IsPaused && idle.IsPaused, "Restart must retain the full queue and paused state.");
            Check(main.Downloaded >= pausedBytes && main.Downloaded > 0, "Restart must retain verified partial download progress.");
            Check(main.IsSequential && idle.IsSequential && service.Settings.SequentialByDefault, "The global mode must persist across restart.");
            Check(service.DownloadDirectory == destination, "The selected download directory must persist across restart.");
            await service.ResumeAsync(main);
            await service.ApplySettingsAsync(service.Settings with { MaxDownloadKiB = 0 });
            await seeder.UpdateSettingsAsync(new EngineSettingsBuilder(seeder.Settings) { MaximumUploadRate = 0 }.ToSettings());
            await UntilAsync(() => main.IsComplete, "Complete restored transfer", token, service);
            var actual = await File.ReadAllBytesAsync(main.Files.Single().FullPath, token);
            Check(SHA256.HashData(actual).AsSpan().SequenceEqual(SHA256.HashData(primary.Bytes)), "The completed restored file must exactly match the source SHA-256.");
            Check(main.PieceMap.All(value => value == 1) && main.CompletedPieces == main.PieceCount, "All pieces must be verified after completion.");
            await service.PauseAsync(main);
            manager = Manager(service, main);
            await manager.HashCheckAsync(false).WaitAsync(TimeSpan.FromSeconds(15), token);
            Check(manager.Complete, "Independent full disk hash check must pass after restart and completion.");
            Console.WriteLine("PASS pause, persisted partial bytes, restart, resume, completed SHA-256 and torrent hash check");

            for (int n = 0; n < 6; n++)
            {
                await service.ResumeAsync(main);
                await service.PauseAsync(main).WaitAsync(TimeSpan.FromSeconds(4), token);
                Check(Manager(service, main).State == TorrentState.Stopped, "Immediate stop must finish after an asynchronous start.");
            }
            Console.WriteLine("PASS six immediate resume/stop cycles without a disposed cancellation source");

            await service.ResumeAsync(main);
            var shutdownWatch = Stopwatch.StartNew();
            await service.DisposeAsync();
            Check(shutdownWatch.Elapsed < TimeSpan.FromSeconds(5), "An active session must close promptly without waiting for tracker timeouts.");
            service = NewService(profile, warnings);
            await service.InitializeAsync();
            main = service.Torrents.Single(item => item.Id == main.Id);
            Check(main.IsComplete && !main.IsPaused, "Shutdown preserves the completed data and intended running state.");
            Console.WriteLine($"PASS active-session shutdown: {shutdownWatch.Elapsed.TotalMilliseconds:F0} ms; restart retains data and running intent");

            var magnet = $"magnet:?xt=urn:btih:{magnetFixture.Torrent.InfoHashes.V1!.ToHex()}&dn=magnet-fixture&x.pe=127.0.0.1:{seedPort}";
            var magnetItem = await service.AddAsync(magnet);
            Check(magnetItem.IsSequential, "A magnet inherits the global sequential mode.");
            await UntilAsync(() => magnetItem.HasMetadata && magnetItem.IsComplete, "Magnet direct-peer metadata and data", token, service);
            var magnetActual = await File.ReadAllBytesAsync(magnetItem.Files.Single().FullPath, token);
            Check(SHA256.HashData(magnetActual).AsSpan().SequenceEqual(SHA256.HashData(magnetFixture.Bytes)), "Direct-peer magnet metadata must lead to exact downloaded bytes.");
            var retainedPath = magnetItem.Files.Single().FullPath;
            // Windows scanners/open torrent files can deny deletion. MonoTorrent's
            // CacheDataOnly unregisters first, then throws on the locked metadata.
            using (var cacheLock = File.Open(Manager(service, magnetItem).MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Task.WhenAll(service.RemoveAsync(magnetItem), service.RemoveAsync(magnetItem)).WaitAsync(TimeSpan.FromSeconds(8), token);
            Check(File.Exists(retainedPath) && !service.Torrents.Contains(magnetItem), "Removing a torrent keeps downloaded user files.");
            service.Refresh();
            Console.WriteLine("PASS magnet data, duplicate removal with locked cache, no disposed-manager row, retained download");
            await service.SaveDetailsHeightAsync(420);
            await service.DisposeAsync();
            service = new TorrentService(profile);
            service.Warning += (_, message) => warnings.Add(message);
            await service.InitializeAsync();
            Check(service.Settings.DetailsPaneHeight == 420, "Details pane height survives restart.");
            Check(service.Torrents.All(t => t.Id != magnetItem.Id), "Removed transfer must stay removed after restart.");
            main = service.Torrents.Single(t => t.Id == main.Id);
            var toDelete = main.Files.Single().FullPath;
            var keepBeside = Path.Combine(Path.GetDirectoryName(toDelete)!, "unrelated-keep.txt");
            await File.WriteAllTextAsync(keepBeside, "not torrent data", token);
            await service.RemoveAsync(main, deleteDownloadedFiles: true).WaitAsync(TimeSpan.FromSeconds(8), token);
            Check(!File.Exists(toDelete) && File.Exists(keepBeside), "Delete-with-files must remove exact torrent data and retain neighboring files.");
            Console.WriteLine("PASS real completed torrent removal with files; unrelated neighboring file retained");
            foreach (var item in service.Torrents.ToArray()) await service.RemoveAsync(item).WaitAsync(TimeSpan.FromSeconds(8), token);
            await service.AddAsync(idleFixture.TorrentPath);
            var activeRemoval = service.RemoveAsync(service.Torrents.Single());
            while (!activeRemoval.IsCompleted) { service.Refresh(); await Task.Delay(20, token); }
            await activeRemoval;
            service.Refresh();
            Check(service.Torrents.Count == 0, "Removing last running transfer keeps UI refresh usable.");
            Console.WriteLine("PASS panel layout persistence; last active torrent removed and empty queue remains responsive");
            Check(warnings.Count == 0, "Unexpected service warning: " + string.Join(" | ", warnings));
        }
        finally
        {
            if (service is not null) await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            await seeder.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    // Explicit opt-in only: an official public fixture can verify WAN discovery.
    // Default smoke tests remain deterministic and communicate exclusively over loopback.
    private static async Task RunPublicTorrentAsync(string[] args)
    {
        Check(args.Length >= 2 && File.Exists(args[1]), "Usage: --public-torrent <official .torrent file> [new artifact directory]");
        var runDirectory = Path.GetFullPath(args.ElementAtOrDefault(2)
            ?? Path.Combine("artifacts", "test-runs", "public-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        Check(!Directory.Exists(runDirectory) || !Directory.EnumerateFileSystemEntries(runDirectory).Any(), "The public-network probe requires a new empty artifact directory.");
        var profile = Path.Combine(runDirectory, "profile");
        var dataDirectory = Directory.CreateDirectory(Path.Combine(profile, "SneakyFlow.Data")).FullName;
        var settings = new AppSettings
        {
            DownloadDirectory = Path.Combine(runDirectory, "download"),
            EnableDht = true,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false,
            ListenPort = 0,
            SequentialByDefault = true,
            MaxDownloadKiB = 512,
            MaxUploadKiB = 64,
        };
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "session.json"), JsonSerializer.Serialize(new { Version = 1, Settings = settings, Torrents = Array.Empty<object>() }));
        Console.WriteLine($"Explicit public-network probe; isolated artifacts: {runDirectory}");
        var warnings = new List<string>();
        await using var service = NewService(profile, warnings);
        await service.InitializeAsync();
        var torrent = await service.AddAsync(Path.GetFullPath(args[1]));
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(60) && torrent.Downloaded < 4 * 1024 * 1024)
            {
                service.Refresh();
                Console.WriteLine($"{elapsed.Elapsed.TotalSeconds:F0}s | verified {torrent.Downloaded:N0} bytes | {torrent.DownloadSpeed:N0} B/s | {torrent.ConnectionSummary} | {torrent.DhtStatus} | {torrent.Diagnosis}");
                await Task.Delay(2000);
            }
            service.Refresh();
            foreach (var tracker in torrent.Trackers) Console.WriteLine($"TRACKER {tracker.Address}: {tracker.Status}; {tracker.Message}");
            foreach (var change in torrent.Events.Take(12).Reverse()) Console.WriteLine($"EVENT {change.TimeText}: {change.Message}");
            Check(torrent.CompletedPieces > 0, $"No verified public-network pieces in 60 seconds: {torrent.Diagnosis}");
            Console.WriteLine($"PASS WAN data verified: {torrent.Downloaded:N0} bytes, {torrent.CompletedPieces} pieces, {elapsed.Elapsed.TotalSeconds:F1}s");
        }
        finally { await service.PauseAsync(torrent); }
    }

    private static TorrentService NewService(string profile, List<string> warnings)
    {
        var service = new TorrentService(profile);
        service.Warning += (_, message) => { warnings.Add(message); Console.WriteLine("SERVICE WARNING: " + message); };
        return service;
    }

    private static async Task VerifyProfileMigrationAsync(string runDirectory)
    {
        var legacyRoot = Path.Combine(runDirectory, "legacy portable app");
        var newRoot = Path.Combine(runDirectory, "migrated profile");
        var legacyCache = Path.Combine(legacyRoot, "SneakyFlow.Data", "Engine", "metadata");
        Directory.CreateDirectory(legacyCache);
        var sourcePath = Path.Combine(legacyCache, "example.torrent");
        await File.WriteAllTextAsync(sourcePath, "synthetic cached metadata");
        var sessionPath = Path.Combine(legacyRoot, "SneakyFlow.Data", "session.json");
        var originalSession = JsonSerializer.Serialize(new
        {
            Settings = new { DownloadDirectory = "Downloads" },
            Torrents = new[] { new { Source = "SneakyFlow.Data/Engine/metadata/example.torrent", SavePath = "Downloads", IsPaused = true } },
        });
        await File.WriteAllTextAsync(sessionPath, originalSession);
        Check(await AppStorage.MigrateLegacyAsync(legacyRoot, newRoot), "Legacy session must migrate into an empty new user profile.");
        var migratedSessionPath = Path.Combine(newRoot, "SneakyFlow.Data", "session.json");
        using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(migratedSessionPath));
        var expectedDownloads = Path.Combine(legacyRoot, "Downloads");
        var expectedSource = Path.Combine(newRoot, "SneakyFlow.Data", "Engine", "metadata", "example.torrent");
        Check(migrated.RootElement.GetProperty("Settings").GetProperty("DownloadDirectory").GetString() == expectedDownloads, "Migration must retain the previous absolute download directory.");
        var torrent = migrated.RootElement.GetProperty("Torrents")[0];
        Check(torrent.GetProperty("SavePath").GetString() == expectedDownloads, "Existing torrent data must stay in its old location.");
        Check(Path.GetFullPath(torrent.GetProperty("Source").GetString()!) == expectedSource && File.Exists(expectedSource), "Migrated torrent source must refer to the copied metadata cache.");
        Check(await File.ReadAllTextAsync(sessionPath) == originalSession && File.Exists(sourcePath), "Migration must leave the legacy session and cache untouched.");
        var firstMigration = await File.ReadAllBytesAsync(migratedSessionPath);
        Check(!await AppStorage.MigrateLegacyAsync(legacyRoot, newRoot), "An existing user profile must never be replaced by migration.");
        var afterSecondMigration = await File.ReadAllBytesAsync(migratedSessionPath);
        Check(firstMigration.AsSpan().SequenceEqual(afterSecondMigration), "Repeated migration must not modify the new profile.");
        Console.WriteLine("PASS legacy profile migration preserves downloads, copies metadata, leaves originals, and is idempotent");
    }

    private sealed record Fixture(Torrent Torrent, string TorrentPath, byte[] Bytes);

    private static async Task<Fixture> CreateFixtureAsync(string runDirectory, string seedDirectory, string name, int length, int randomSeed, string? trackerUrl, CancellationToken token)
    {
        var bytes = new byte[length];
        new Random(randomSeed).NextBytes(bytes);
        var path = Path.Combine(seedDirectory, name);
        await File.WriteAllBytesAsync(path, bytes, token);
        var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 };
        if (trackerUrl is not null) creator.Announces.Add(new List<string> { trackerUrl });
        var metadata = await creator.CreateAsync(new TorrentFileSource(path), token);
        var torrentPath = Path.Combine(runDirectory, name + ".torrent");
        await File.WriteAllBytesAsync(torrentPath, metadata.Encode(), token);
        return new(Torrent.Load(metadata), torrentPath, bytes);
    }

    // Only observe the engine for wire events and an independent hash check.
    // All other mutations under test go through public TorrentService APIs.
    private static TorrentManager Manager(TorrentService service, TorrentItem item)
    {
        var engine = (ClientEngine?)typeof(TorrentService).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);
        return engine!.Torrents.Single(manager => (manager.InfoHashes.V1 ?? manager.InfoHashes.V2!).ToHex() == item.Id);
    }

    private static async Task VerifyPrefixAsync(string path, byte[] expected, CancellationToken token)
    {
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
        var prefix = new byte[1024 * 1024];
        await reader.ReadExactlyAsync(prefix, token);
        Check(prefix.AsSpan().SequenceEqual(expected.AsSpan(0, prefix.Length)), "An external reader must see exact verified prefix bytes before completion, without a forced cache flush.");
        reader.Seek(700_001, SeekOrigin.Begin);
        var seekBytes = new byte[4096];
        await reader.ReadExactlyAsync(seekBytes, token);
        Check(seekBytes.AsSpan().SequenceEqual(expected.AsSpan(700_001, seekBytes.Length)), "Seeking inside the downloaded prefix must return exact bytes.");
    }

    private static async Task UntilAsync(Func<bool> predicate, string description, CancellationToken token, TorrentService? service = null)
    {
        var elapsed = Stopwatch.StartNew();
        var lastReport = 0;
        while (true)
        {
            service?.Refresh();
            if (predicate()) return;
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(50))
                throw new TimeoutException(description + (service is null ? "" : ": " + string.Join(" | ", service.Torrents.Select(t => $"{t.Name}: {t.State}, {t.Progress:F2}%, {t.Diagnosis}, {t.TrackerSummary}"))));
            if (service is not null && (int)elapsed.Elapsed.TotalSeconds / 10 > lastReport)
            {
                lastReport = (int)elapsed.Elapsed.TotalSeconds / 10;
                Console.WriteLine($"WAIT {description}: " + string.Join(" | ", service.Torrents.Select(t => $"{t.Name}: {t.Progress:F2}%, {t.ConnectionSummary}; {t.Diagnosis}")));
            }
            await Task.Delay(75, token);
        }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class TestSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void Complete() => _queue.CompleteAdding();
        public void Pump() { foreach (var work in _queue.GetConsumingEnumerable()) work.Callback(work.State); }
        public void Dispose() => _queue.Dispose();
    }

    // Override only interval timing; all responses still come over real loopback HTTP.
    // After one permitted request, prevent an automatic announce from masking a
    // broken manual retry which silently discards its returned peers.
    private sealed class RetryTracker(ITracker inner) : ITracker
    {
        public bool AllowNextAnnounce { get; set; }
        public bool CanScrape => inner.CanScrape;
        public TimeSpan MinUpdateInterval => TimeSpan.Zero;
        public TimeSpan UpdateInterval => AllowNextAnnounce ? TimeSpan.Zero : TimeSpan.FromHours(1);
        public TimeSpan TimeSinceLastAnnounce => inner.TimeSinceLastAnnounce;
        public TrackerState Status => inner.Status;
        public Uri Uri => inner.Uri;
        public string WarningMessage => inner.WarningMessage;
        public string FailureMessage => inner.FailureMessage;
        public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest request, CancellationToken token)
        {
            AllowNextAnnounce = false;
            return await inner.AnnounceAsync(request, token);
        }
        public ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest request, CancellationToken token) => inner.ScrapeAsync(request, token);
    }

    // Speak real tracker HTTP/bencode over loopback. Advertised swarm estimates
    // intentionally differ from active connections to verify honest statistics.
    private sealed class LocalTracker : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly int _seedPort;
        private volatile bool _announcePeer;
        public bool AnnouncePeer { get => _announcePeer; set => _announcePeer = value; }
        public string Url { get; }

        public LocalTracker(int seedPort)
        {
            _seedPort = seedPort;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/announce";
            _server = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var request = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await request.ReadLineAsync(_stop.Token) ?? "";
                    Console.WriteLine($"LOCAL TRACKER {requestLine.Split(' ').ElementAtOrDefault(1)?.Split('?')[0]} peers={(AnnouncePeer ? 1 : 0)}");
                    while (!string.IsNullOrEmpty(await request.ReadLineAsync(_stop.Token))) { }
                    var compactPeer = AnnouncePeer ? new byte[] { 127, 0, 0, 1, (byte)(_seedPort >> 8), (byte)_seedPort } : [];
                    var dictionary = new BEncodedDictionary
                    {
                        ["interval"] = new BEncodedNumber(2),
                        ["min interval"] = new BEncodedNumber(1),
                        ["complete"] = new BEncodedNumber(5),
                        ["incomplete"] = new BEncodedNumber(7),
                        ["peers"] = new BEncodedString(compactPeer),
                    };
                    if (requestLine.Contains("/scrape?", StringComparison.Ordinal))
                    {
                        var files = new BEncodedDictionary();
                        var target = requestLine.Split(' ')[1];
                        foreach (var query in target[(target.IndexOf('?') + 1)..].Split('&').Where(part => part.StartsWith("info_hash=")))
                        {
                            var encoded = query[10..];
                            var hash = new List<byte>();
                            for (int i = 0; i < encoded.Length; i++)
                            {
                                if (encoded[i] == '%') { hash.Add(Convert.ToByte(encoded.Substring(i + 1, 2), 16)); i += 2; }
                                else hash.Add((byte)(encoded[i] == '+' ? ' ' : encoded[i]));
                            }
                            files[new BEncodedString(hash.ToArray())] = new BEncodedDictionary
                            {
                                ["complete"] = new BEncodedNumber(5),
                                ["incomplete"] = new BEncodedNumber(7),
                                ["downloaded"] = new BEncodedNumber(9),
                            };
                        }
                        dictionary = new BEncodedDictionary { ["files"] = files };
                    }
                    var response = dictionary.Encode();
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _server;
            _stop.Dispose();
        }
    }
}
