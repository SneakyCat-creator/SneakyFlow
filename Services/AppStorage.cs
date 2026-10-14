using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SneakyFlow.Services;

/// <summary>Settings belong to the Windows user, not the self-extracting executable directory.</summary>
public static class AppStorage
{
    public static string DefaultProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SneakyFlow");

    public static async Task<bool> MigrateLegacyAsync(string executableDirectory, string profileDirectory, string? sourceDataName = null)
    {
        var sourceRoot = Path.GetFullPath(executableDirectory);
        var targetRoot = Path.GetFullPath(profileDirectory);
        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var sourceData = Path.Combine(sourceRoot, sourceDataName ?? "SneakyFlow.Data");
        var targetData = Path.Combine(targetRoot, "SneakyFlow.Data");
        var sourceSession = Path.Combine(sourceData, "session.json");
        var targetSession = Path.Combine(targetData, "session.json");
        if (!File.Exists(sourceSession) || File.Exists(targetSession)) return false;

        // Parse before copying. A broken session must not silently replace the user's working profile.
        var session = JsonNode.Parse(await File.ReadAllTextAsync(sourceSession))?.AsObject()
            ?? throw new InvalidDataException(L.T("Не удалось прочитать прежнюю очередь SneakyFlow."));
        if (session["Settings"] is JsonObject settings && settings["DownloadDirectory"]?.GetValue<string>() is { } downloads)
            settings["DownloadDirectory"] = Path.GetFullPath(downloads, sourceRoot);
        if (session["Torrents"] is JsonArray torrents)
        {
            foreach (var torrent in torrents.OfType<JsonObject>())
            {
                if (torrent["SavePath"]?.GetValue<string>() is { } savePath)
                    torrent["SavePath"] = Path.GetFullPath(savePath, sourceRoot);
                if (torrent["Source"]?.GetValue<string>() is { } source && !source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var absoluteSource = Path.GetFullPath(source, sourceRoot);
                    var relative = Path.GetRelativePath(sourceData, absoluteSource);
                    var insideCache = !Path.IsPathRooted(relative) && relative != ".." &&
                        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                    torrent["Source"] = insideCache ? Path.Combine(targetData, relative) : absoluteSource;
                }
            }
        }

        Directory.CreateDirectory(targetData);
        CopyCache(sourceData, targetData);
        await File.WriteAllTextAsync(targetSession + ".migrating", session.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(targetSession + ".migrating", targetSession, overwrite: false);
        await File.WriteAllTextAsync(Path.Combine(targetData, "migration.txt"),
            L.F($"{DateTimeOffset.Now:O}\nПрофиль скопирован из {sourceData}.\nИсходный профиль и загруженные файлы сохранены на прежнем месте.\n"));
        return true;
    }

    private static void CopyCache(string source, string destination)
    {
        // Avoid following junctions out of the old application data directory.
        foreach (var file in new DirectoryInfo(source).EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.Name.Equals("session.json", StringComparison.OrdinalIgnoreCase) ||
                file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            // No target session exists yet, so retry an interrupted migration safely.
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: true);
        }
        foreach (var directory in new DirectoryInfo(source).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var target = Directory.CreateDirectory(Path.Combine(destination, directory.Name)).FullName;
            CopyCache(directory.FullName, target);
        }
    }
}
