using System.Diagnostics;
using MonoTorrent.PortForwarding;
using SneakyFlow;
using SneakyFlow.Services;

internal static class UserExperienceChecks
{
    public static async Task RunAsync(string root)
    {
        if (new SneakyFlow.Models.AppSettings().SequentialByDefault || new SneakyFlow.Models.AppSettings().AdvancedLogging)
            throw new Exception("Sequential downloading and advanced logging must default to off.");
        var recovery = new StallRecovery();
        var now = DateTimeOffset.UtcNow;
        if (recovery.TryRecover(now, TimeSpan.FromMinutes(3), false) || !recovery.TryRecover(now, TimeSpan.FromMinutes(3), true) ||
            recovery.TryRecover(now.AddMinutes(1), TimeSpan.FromMinutes(4), true) ||
            !recovery.TryRecover(now.AddMinutes(10), TimeSpan.FromMinutes(14), true) ||
            !recovery.TryRecover(now.AddMinutes(20), TimeSpan.FromMinutes(24), true) ||
            recovery.TryRecover(now.AddMinutes(30), TimeSpan.FromMinutes(34), true)) throw new Exception("Recovery would loop.");
        var deleteRoot = Directory.CreateDirectory(Path.Combine(root, "deletion")).FullName;
        var owned = Path.Combine(deleteRoot, "download.bin");
        var unrelated = Path.Combine(deleteRoot, "keep.txt");
        await File.WriteAllTextAsync(owned, "owned"); await File.WriteAllTextAsync(unrelated, "unrelated");
        var errors = DownloadDeletion.DeleteFiles(deleteRoot, [owned, Path.Combine(root, "outside.bin")]);
        if (File.Exists(owned) || !File.Exists(unrelated) || errors.Count != 1) throw new Exception("Deletion scope failed.");
        Console.WriteLine("PASS conservative defaults, bounded recovery, exact download deletion scope");
        var router = new UnresponsiveRouter();
        var forwarding = new ShutdownPortForwarder(router);
        await forwarding.StartAsync(CancellationToken.None);
        if (!router.Started) throw new Exception("Normal forwarding was not delegated");
        forwarding.BeginShutdown();
        var timer = Stopwatch.StartNew();
        await Task.WhenAll(forwarding.StopAsync(CancellationToken.None), forwarding.StopAsync(true, CancellationToken.None));
        if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Router cleanup blocked shutdown");
        router.Response.TrySetResult();
        Console.WriteLine($"PASS unresponsive router cleanup bounded: {timer.Elapsed.TotalMilliseconds:F0} ms");

        var profile = Path.Combine(root, "language-profile");
        Directory.CreateDirectory(Path.Combine(profile, "SneakyFlow.Data"));
        await File.WriteAllTextAsync(Path.Combine(profile, "SneakyFlow.Data", "session.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Settings = new SneakyFlow.Models.AppSettings {
                DownloadDirectory = Path.Combine(profile, "downloads"), EnableDht = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false } }));
        await using (var service = new TorrentService(profile))
        {
            await service.InitializeAsync();
            await service.ApplySettingsAsync(service.Settings with { Language = "en", UiScale = 1.25, EnableDht = false, EnablePortForwarding = false, EnableLocalPeerDiscovery = false });
            if (L.T("На паузе") != "Paused" || L.F($"Данные получены {5} назад") != "Data received 5 ago") throw new Exception("English diagnostics failed");
            if (L.Existing("Пир подключён: 127.0.0.1:6881 · Outgoing") != "Peer connected: 127.0.0.1:6881 · Outgoing") throw new Exception("Retained journal translation failed");
        }
        await using (var restored = new TorrentService(profile))
        {
            await restored.InitializeAsync();
            if (restored.Settings.Language != "en" || L.Language != "en") throw new Exception("Language did not survive restart");
            if (restored.Settings.UiScale != 1.25) throw new Exception("UI scale did not survive restart");
        }
        L.SetLanguage("ru");
        Console.WriteLine("PASS English diagnostics, language and UI scale persistence across restart");
    }

    private sealed class UnresponsiveRouter : IPortForwarder
    {
        public TaskCompletionSource Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started;
        public bool Active => Started;
        public Mappings Mappings => throw new NotSupportedException();
        public event EventHandler MappingsChanged { add { } remove { } }
        public Task RegisterMappingAsync(Mapping mapping) => Task.CompletedTask;
        public Task UnregisterMappingAsync(Mapping mapping, CancellationToken token) => Response.Task;
        public Task StartAsync(CancellationToken token) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) => Response.Task;
        public Task StopAsync(bool removeExistingMappings, CancellationToken token) => Response.Task;
    }
}
