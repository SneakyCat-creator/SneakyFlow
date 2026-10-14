using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SneakyFlow.Services;

public static class MaintenanceLease
{
    public static string PathFor(string profile)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(profile).ToUpperInvariant())))[..20];
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SneakyFlow", "maintenance-" + key + ".lock");
    }
    public static FileStream Acquire(string profile)
    {
        var path = PathFor(profile);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        DownloadDeletion.ValidatePath(System.IO.Path.GetTempPath(), path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
    public static bool IsBusy(string profile)
    {
        if (!File.Exists(PathFor(profile))) return false;
        try { using var lease = Acquire(profile); return false; }
        catch (IOException) { return true; }
    }
}
