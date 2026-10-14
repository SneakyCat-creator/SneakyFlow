using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SneakyFlow.Services;

public sealed record ReleaseInfo(string Tag, Version Version, Uri ExeUrl, string Sha256, long Size);

public sealed class ReleaseClient(HttpClient client)
{
    public const string Repository = "SneakyCat-creator/SneakyFlow";
    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SneakyFlow/1.5");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
    public async Task<ReleaseInfo?> CheckAsync(Version current, CancellationToken token, bool currentIsPrerelease = false)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var release = doc.RootElement;
        var tag = release.GetProperty("tag_name").GetString()!;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            !Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version) ||
            version < current || (version == current && !currentIsPrerelease)) return null;
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var executables = assets.Where(a => a.GetProperty("name").GetString() == "SneakyFlow.exe").ToArray();
        if (executables.Length != 1) throw new InvalidDataException("The official executable is missing or ambiguous.");
        var exe = executables[0];
        var exeUrl = ValidateAssetUrl(exe.GetProperty("browser_download_url").GetString()!, tag, "SneakyFlow.exe");
        // GitHub computes this SHA-256 from the uploaded bytes. Fail closed if it is absent.
        if (!exe.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            digest.GetString() is not { } digestText || !Regex.IsMatch(digestText, "^sha256:[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("The official asset has no valid SHA-256 digest.");
        if (!exe.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size < 1 || size > 200_000_000)
            throw new InvalidDataException("Invalid release size.");
        return new(tag, version, exeUrl, digestText[7..], size);
    }
    internal static Uri ValidateAssetUrl(string url, string tag, string name)
    {
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        if (!string.Equals(url, expected, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release asset URL.");
        return new Uri(url);
    }
    public async Task DownloadAsync(ReleaseInfo release, string target, CancellationToken token, IProgress<UpdateProgress>? progress = null)
    {
        bool created = false;
        try
        {
            progress?.Report(new(UpdateStage.Downloading, 0));
            using var response = await client.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                created = true;
                var buffer = new byte[128 * 1024]; long total = 0; int count, lastPercent = 0;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("Release exceeds declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    // EOF and the declared length must agree before showing 100%.
                    var percent = (int)Math.Min(99, total * 100 / release.Size);
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(new(UpdateStage.Downloading, percent)); }
                }
                if (total != release.Size) throw new InvalidDataException("Incomplete release download.");
            }
            progress?.Report(new(UpdateStage.Downloading, 100));
            progress?.Report(new(UpdateStage.Verifying));
            if (!string.Equals(await ProductStateStore.HashAsync(target), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 verification failed.");
            var info = FileVersionInfo.GetVersionInfo(target);
            if (info.OriginalFilename != "SneakyFlow.dll" ||
                new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != release.Version)
                throw new InvalidDataException("Executable identity/version does not match its release tag.");
        }
        catch { if (created && File.Exists(target)) File.Delete(target); throw; }
    }
}
