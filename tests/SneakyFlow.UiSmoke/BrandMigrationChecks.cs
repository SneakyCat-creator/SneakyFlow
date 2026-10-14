using System.IO;
using System.Text.Json;
using SneakyFlow.Services;

internal static class BrandMigrationChecks
{
    public static async Task RunAsync(string output)
    {
        var root = Path.Combine(output, "brand-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "PreviousProduct");
        var data = Path.Combine(source, "PreviousProduct.Data");
        Directory.CreateDirectory(Path.Combine(data, "Engine"));
        var target = Path.Combine(root, "SneakyFlow");
        var downloads = Path.Combine(root, "my downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "keep.mkv"), "existing media");
        File.WriteAllText(Path.Combine(data, "Engine", "fixture.torrent"), "metadata");
        File.WriteAllText(Path.Combine(data, "window.json"), "{\"Width\":1400}");
        var original = JsonSerializer.Serialize(new { Settings = new { DownloadDirectory = downloads, Language = "en", UiScale = 1.25 },
            Torrents = new[] { new { Source = Path.Combine(data, "Engine", "fixture.torrent"), SavePath = downloads, IsPaused = true } } });
        File.WriteAllText(Path.Combine(data, "session.json"), original);
        var acceptedAt = DateTimeOffset.UtcNow.AddDays(-1);
        File.WriteAllText(Path.Combine(source, "product-state.json"), JsonSerializer.Serialize(new ProductState {
            AcceptedLicenseVersion = LicensePolicy.Version, AcceptedLicenseHash = BrandMigration.PreviousLicenseHash, AcceptedAt = acceptedAt }));
        if (!await Task.Run(() => BrandMigration.Import(root, target))) throw new Exception("Predecessor profile was not imported.");
        var state = await new ProductStateStore(target).ReadAsync();
        if (!LicensePolicy.IsAccepted(state) || state.AcceptedAt != acceptedAt) throw new Exception("Brand-only migration lost valid consent.");
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "SneakyFlow.Data", "session.json")));
        var settings = session.RootElement.GetProperty("Settings");
        if (settings.GetProperty("Language").GetString() != "en" || settings.GetProperty("UiScale").GetDouble() != 1.25 ||
            settings.GetProperty("DownloadDirectory").GetString() != downloads) throw new Exception("Migration lost user settings.");
        var migratedSource = session.RootElement.GetProperty("Torrents")[0].GetProperty("Source").GetString();
        if (migratedSource != Path.Combine(target, "SneakyFlow.Data", "Engine", "fixture.torrent") || !File.Exists(migratedSource))
            throw new Exception("Metadata path still points to the staging profile.");
        if (File.ReadAllText(Path.Combine(data, "session.json")) != original || File.ReadAllText(Path.Combine(downloads, "keep.mkv")) != "existing media" ||
            File.ReadAllText(Path.Combine(target, "SneakyFlow.Data", "window.json")) != "{\"Width\":1400}") throw new Exception("Migration damaged existing data or window state.");
        if (await Task.Run(() => BrandMigration.Import(root, target))) throw new Exception("An existing profile was overwritten.");
        var unaccepted = Path.Combine(output, "unaccepted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(unaccepted, "PreviousProduct", "PreviousProduct.Data"));
        File.WriteAllText(Path.Combine(unaccepted, "PreviousProduct", "product-state.json"), JsonSerializer.Serialize(new ProductState()));
        File.WriteAllText(Path.Combine(unaccepted, "PreviousProduct", "PreviousProduct.Data", "session.json"), original);
        if (await Task.Run(() => BrandMigration.Import(unaccepted, Path.Combine(unaccepted, "SneakyFlow")))) throw new Exception("Migration invented license acceptance.");
        Console.WriteLine("PASS brand migration preserves queue, cache paths, settings, window layout, consent and original files");
    }
}
