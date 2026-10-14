using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SneakyFlow.Services;

/// <summary>Import one recognized predecessor profile without relying on its brand or moving downloads.</summary>
internal static class BrandMigration
{
    // The terms changed only their product name. No new permissions or obligations were added.
    internal const string PreviousLicenseHash = "5E773E1C0A55DE2A9157E782987164B168AA02B7D2EA36E26D3925181EC9B843";

    internal static bool Import(string localAppData, string destination)
    {
        localAppData = Path.GetFullPath(localAppData);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || !Directory.Exists(localAppData)) return false;
        var matches = new List<(DirectoryInfo Directory, ProductState State)>();
        foreach (var candidate in new DirectoryInfo(localAppData).EnumerateDirectories())
        {
            if ((candidate.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var statePath = Path.Combine(candidate.FullName, "product-state.json");
            var sessionPath = Path.Combine(candidate.FullName, candidate.Name + ".Data", "session.json");
            if (!File.Exists(statePath) || !File.Exists(sessionPath)) continue;
            try
            {
                DownloadDeletion.ValidatePath(localAppData, statePath);
                DownloadDeletion.ValidatePath(localAppData, sessionPath);
                var state = JsonSerializer.Deserialize<ProductState>(File.ReadAllText(statePath));
                if (state?.AcceptedLicenseVersion == LicensePolicy.Version && state.AcceptedAt is not null &&
                    string.Equals(state.AcceptedLicenseHash, PreviousLicenseHash, StringComparison.OrdinalIgnoreCase))
                    matches.Add((candidate, state));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (matches.Count == 0) return false;
        if (matches.Count != 1) throw new IOException(L.T("Найдено несколько прежних профилей. Перенос отменён, чтобы не выбрать неверную очередь."));
        var (source, previous) = matches[0];
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.FullName.ToUpperInvariant())))[..20];
        using var guard = new Mutex(false, @"Local\" + source.Name + "-" + key);
        bool acquired;
        try { acquired = guard.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException(L.T("Закройте прежнюю версию программы и запустите SneakyFlow снова для переноса очереди."));
        string staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            // Synchronous worker: the named mutex stays on the owning thread throughout the import.
            DownloadDeletion.ValidatePath(localAppData, Path.Combine(staging, "product-state.json"));
            if (!AppStorage.MigrateLegacyAsync(source.FullName, staging, source.Name + ".Data").GetAwaiter().GetResult()) return false;
            var sessionPath = Path.Combine(staging, "SneakyFlow.Data", "session.json");
            var session = JsonNode.Parse(File.ReadAllText(sessionPath))!.AsObject();
            if (session["Torrents"] is JsonArray torrents)
                foreach (var torrent in torrents.OfType<JsonObject>())
                    if (torrent["Source"]?.GetValue<string>() is { } path && Path.IsPathRooted(path))
                    {
                        var relative = Path.GetRelativePath(staging, path);
                        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                            torrent["Source"] = Path.Combine(destination, relative);
                    }
            File.WriteAllText(sessionPath, session.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // New-brand removal owns only copies and runtime caches registered by the new application.
            var migrated = previous with { AcceptedLicenseHash = LicensePolicy.DocumentHash, Copies = [], RuntimeCaches = [],
                FailedUpdateVersion = null, FailedUpdateAt = null };
            File.WriteAllText(Path.Combine(staging, "product-state.json"), JsonSerializer.Serialize(migrated));
            Directory.Move(staging, destination); // Commit the entire imported profile atomically; never replace one.
            return true;
        }
        finally
        {
            guard.ReleaseMutex();
            if (Directory.Exists(staging))
            {
                // Only our unique staging directory is eligible; the source profile is always retained.
                DownloadDeletion.ValidatePath(localAppData, Path.Combine(staging, "product-state.json"));
                Directory.Delete(staging, true);
            }
        }
    }
}
