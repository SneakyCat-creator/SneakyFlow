using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SneakyFlow.Services;

public static class LicensePolicy
{
    // Independent of the application version. Change when the terms change, not on every release.
    public const string Version = "2026-10-14.1";
    public static string DocumentHash
    {
        get
        {
            using var stream = typeof(LicensePolicy).Assembly.GetManifestResourceStream("SneakyFlow.License.txt")
                ?? throw new InvalidOperationException("License document missing.");
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
    public static bool IsAccepted(ProductState state) => state.AcceptedLicenseVersion == Version &&
        state.AcceptedLicenseHash == DocumentHash && state.AcceptedAt is not null;
}

public sealed record KnownCopy(string Path, string Sha256);
public sealed record KnownCache(string Path, string AssemblySha256);
public sealed record ProductState
{
    public string? AcceptedLicenseVersion { get; init; }
    public string? AcceptedLicenseHash { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public string? FailedUpdateVersion { get; init; }
    public DateTimeOffset? FailedUpdateAt { get; init; }
    public List<KnownCopy> Copies { get; init; } = [];
    public List<KnownCache> RuntimeCaches { get; init; } = [];
}

public sealed class ProductStateStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    private string StatePath => Path.Combine(Root, "product-state.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<ProductState> ReadAsync()
    {
        try { return JsonSerializer.Deserialize<ProductState>(await File.ReadAllTextAsync(StatePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    private async Task ChangeAsync(Func<ProductState, ProductState> change)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Root);
            // All executable copies share this state. Serialize read/modify/replace across processes.
            await using var fileLock = await LockAsync();
            var state = change(await ReadAsync());
            var temp = StatePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, StatePath, true);
        }
        finally { _gate.Release(); }
    }
    private async Task<FileStream> LockAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(Path.Combine(Root, "product-state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
        }
    }
    public Task AcceptAsync() => ChangeAsync(s => s with { AcceptedLicenseVersion = LicensePolicy.Version,
        AcceptedLicenseHash = LicensePolicy.DocumentHash, AcceptedAt = DateTimeOffset.UtcNow });
    public Task RevokeAsync() => ChangeAsync(s => s with { AcceptedLicenseVersion = null, AcceptedLicenseHash = null, AcceptedAt = null });
    public Task RecordUpdateFailureAsync(string version) => ChangeAsync(s => s with { FailedUpdateVersion = version, FailedUpdateAt = DateTimeOffset.UtcNow });
    public async Task RegisterCopyAsync(string path)
    {
        path = Path.GetFullPath(path);
        var hash = await HashAsync(path);
        await ChangeAsync(s => s with { Copies = s.Copies.Where(c => !c.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Append(new KnownCopy(path, hash)).ToList() });
    }
    public async Task RegisterRuntimeCacheAsync(string assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        var extractRoot = Path.Combine(Path.GetTempPath(), ".net");
        if (!directory.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        var relative = Path.GetRelativePath(extractRoot, directory).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 2 || relative.Any(p => p is ".." or "." || p.Length == 0)) return;
        DownloadDeletion.ValidatePath(extractRoot, assemblyPath);
        var hash = await HashAsync(assemblyPath);
        await ChangeAsync(s => s with { RuntimeCaches = s.RuntimeCaches.Where(c => !c.Path.Equals(directory, StringComparison.OrdinalIgnoreCase))
            .Append(new KnownCache(directory, hash)).ToList() });
    }
    public static async Task<string> HashAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }
}
