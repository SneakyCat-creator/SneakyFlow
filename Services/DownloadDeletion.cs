using System.IO;

namespace SneakyFlow.Services;

public static class DownloadDeletion
{
    public static void ValidatePath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Deletion path is outside the torrent download directory.");
        // Reject junctions/symlinks, including ancestors of the chosen root.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Deletion through a symbolic link or junction is not allowed: " + current);
    }

    public static IReadOnlyList<string> DeleteFiles(string root, IEnumerable<string> files)
    {
        var errors = new List<string>();
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase))
            try { ValidatePath(root, path); File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(path + ": " + ex.Message); }
        // Do not recursively remove the containing folder: it can hold unrelated user files.
        return errors;
    }
}
