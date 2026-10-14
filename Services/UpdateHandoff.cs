using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SneakyFlow.Services;

public sealed record UpdateTicket(int ParentPid, long ParentStartTicks, string Target, string OldHash, string NewHash, string Version,
    Dictionary<string, string>? SettingsDraft = null);

public static class UpdateHandoff
{
    public static string OperationRoot => Path.Combine(Path.GetTempPath(), "SneakyFlow", "operations");
    public static string CreateDirectory()
    {
        var directory = Path.Combine(OperationRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DownloadDeletion.ValidatePath(Path.GetTempPath(), Path.Combine(directory, "work.json"));
        return directory;
    }
    public static async Task StartAsync(string directory, ReleaseInfo release, string target, CancellationToken cancellationToken = default,
        IProgress<UpdateProgress>? progress = null, Dictionary<string, string>? settingsDraft = null)
    {
        progress?.Report(new(UpdateStage.Installing, 0));
        var process = Process.GetCurrentProcess();
        var ticket = new UpdateTicket(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(target),
            await ProductStateStore.HashAsync(target), release.Sha256, release.Version.ToString(), settingsDraft);
        var ticketPath = Path.Combine(directory, "work.json");
        await File.WriteAllTextAsync(ticketPath, JsonSerializer.Serialize(ticket));
        var start = new ProcessStartInfo(Path.Combine(directory, "SneakyFlow.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(ticketPath);
        start.ArgumentList.Add(await ProductStateStore.HashAsync(ticketPath));
        Process? helper = null;
        bool ownsStaged = false;
        try
        {
            // Copy while the current Settings window is still alive. The helper only needs
            // the atomic replacement after exit; the running executable is never overwritten.
            await StageAsync(ticket, start.FileName, directory, progress, cancellationToken);
            ownsStaged = true;
            helper = Process.Start(start) ?? throw new IOException("Could not start the new version.");
            for (int n = 0; n < 150; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(directory, "ready")))
                {
                    progress?.Report(new(UpdateStage.Installing, 98, L.T("Сохраняем очередь и перезапускаем программу…")));
                    await File.WriteAllTextAsync(Path.Combine(directory, "proceed"), "proceed", cancellationToken);
                    return;
                }
                if (helper.HasExited) throw new IOException(await ReadErrorAsync(directory));
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("The updater did not confirm readiness. The current program is still running.");
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "cancel"), "cancel");
            // Before proceed, the helper can only wait; it cannot replace this staged file.
            var staged = StagedPath(ticket, directory);
            if (ownsStaged && File.Exists(staged)) File.Delete(staged);
            throw;
        }
        finally { helper?.Dispose(); }
    }
    private static async Task<string> ReadErrorAsync(string directory) => File.Exists(Path.Combine(directory, "error.txt"))
        ? await File.ReadAllTextAsync(Path.Combine(directory, "error.txt")) : "The updater could not start.";

    public static string ValidateDirectory(string ticketPath)
    {
        ticketPath = Path.GetFullPath(ticketPath);
        var directory = Path.GetDirectoryName(ticketPath)!;
        if (Path.GetFileName(ticketPath) != "work.json" || Path.GetDirectoryName(directory) != OperationRoot ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new IOException("Invalid update handoff directory.");
        DownloadDeletion.ValidatePath(Path.GetTempPath(), ticketPath);
        return directory;
    }
    public static async Task<int> ApplyAsync(string ticketPath, string ticketHash)
    {
        var directory = ValidateDirectory(ticketPath);
        UpdateTicket? verifiedTicket = null;
        bool parentExited = false;
        try
        {
            if (!string.Equals(await ProductStateStore.HashAsync(ticketPath), ticketHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update handoff was modified.");
            var ticket = JsonSerializer.Deserialize<UpdateTicket>(await File.ReadAllTextAsync(ticketPath))!;
            var helperPath = Path.Combine(directory, "SneakyFlow.exe");
            if (!string.Equals(Environment.ProcessPath, helperPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(await ProductStateStore.HashAsync(helperPath), ticket.NewHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected updater executable.");
            var state = await new ProductStateStore(AppStorage.DefaultProfileDirectory).ReadAsync();
            if (!state.Copies.Any(c => string.Equals(c.Path, ticket.Target, StringComparison.OrdinalIgnoreCase) && c.Sha256 == ticket.OldHash))
                throw new IOException("The target is not a registered SneakyFlow copy.");
            DownloadDeletion.ValidatePath(Path.GetDirectoryName(ticket.Target)!, ticket.Target);
            using var maintenance = MaintenanceLease.Acquire(AppStorage.DefaultProfileDirectory);
            using var parent = Process.GetProcessById(ticket.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != ticket.ParentStartTicks ||
                !string.Equals(parent.MainModule?.FileName, ticket.Target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The original process identity changed.");
            verifiedTicket = ticket;
            await File.WriteAllTextAsync(Path.Combine(directory, "ready"), "ready");
            await WaitForCommitAsync(directory);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            parentExited = true;
            await ReplaceAsync(ticket, helperPath, directory);
            maintenance.Dispose(); // Let the new normal instance start; the target has already been replaced.
            var start = new ProcessStartInfo(ticket.Target) { UseShellExecute = false };
            start.ArgumentList.Add("--finish-update"); start.ArgumentList.Add(ticketPath); start.ArgumentList.Add(ticketHash);
            try { using var child = Process.Start(start) ?? throw new IOException("Could not start the updated app."); }
            catch { RestoreBackup(ticket, directory); throw; }
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), ex.Message);
            if (parentExited && verifiedTicket is { } ticket)
            {
                try
                {
                    await new ProductStateStore(AppStorage.DefaultProfileDirectory).RecordUpdateFailureAsync(ticket.Version);
                    if (await ProductStateStore.HashAsync(ticket.Target) == ticket.OldHash)
                    {
                        var restart = new ProcessStartInfo(ticket.Target) { UseShellExecute = false };
                        restart.ArgumentList.Add("--update-failed"); restart.ArgumentList.Add(ticketPath); restart.ArgumentList.Add(ticketHash);
                        using var original = Process.Start(restart);
                    }
                }
                catch (Exception restartError) { await File.AppendAllTextAsync(Path.Combine(directory, "error.txt"), "\n" + restartError.Message); }
            }
            return 1;
        }
    }
    internal static string BackupPath(UpdateTicket ticket, string directory) => ticket.Target + ".previous-" + Path.GetFileName(directory);
    internal static string StagedPath(UpdateTicket ticket, string directory) => ticket.Target + ".update-" + Path.GetFileName(directory);
    internal static async Task StageAsync(UpdateTicket ticket, string helperPath, string directory,
        IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        var staged = StagedPath(ticket, directory);
        DownloadDeletion.ValidatePath(Path.GetDirectoryName(ticket.Target)!, staged);
        bool created = false;
        try
        {
            await using (var input = new FileStream(helperPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                created = true;
                var buffer = new byte[128 * 1024]; long copied = 0; int count, lastPercent = -1;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    copied += count;
                    // 0–90: actual staged bytes; 95: verified; 98: helper ready;
                    // 100 is reserved for the restarted, installed application.
                    var percent = (int)(90 * copied / Math.Max(1, input.Length));
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(new(UpdateStage.Installing, percent)); }
                }
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (!string.Equals(await ProductStateStore.HashAsync(staged), ticket.NewHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged update is corrupted.");
            token.ThrowIfCancellationRequested();
            progress?.Report(new(UpdateStage.Installing, 95));
        }
        catch { if (created && File.Exists(staged)) File.Delete(staged); throw; }
    }
    internal static async Task ReplaceAsync(UpdateTicket ticket, string helperPath, string directory)
    {
        if (!string.Equals(await ProductStateStore.HashAsync(ticket.Target), ticket.OldHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The original executable changed during the update.");
        var staged = StagedPath(ticket, directory);
        var backup = BackupPath(ticket, directory);
        bool ownsStaged = false;
        try
        {
            // Older versions do not pre-stage their update; keep their handoff compatible.
            if (!File.Exists(staged)) await StageAsync(ticket, helperPath, directory);
            if (!string.Equals(await ProductStateStore.HashAsync(staged), ticket.NewHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged update is corrupted.");
            ownsStaged = true;
            File.Replace(staged, ticket.Target, backup); // Atomic replacement on the destination volume.
        }
        finally { if (ownsStaged && File.Exists(staged)) File.Delete(staged); }
    }
    internal static void RestoreBackup(UpdateTicket ticket, string directory)
    {
        var backup = BackupPath(ticket, directory);
        if (File.Exists(backup)) File.Move(backup, ticket.Target, true);
    }
    public static async Task<UpdateTicket> FinishAsync(string ticketPath, string ticketHash)
    {
        var directory = ValidateDirectory(ticketPath);
        if (await ProductStateStore.HashAsync(ticketPath) != ticketHash) throw new IOException("Invalid cleanup handoff.");
        var ticket = JsonSerializer.Deserialize<UpdateTicket>(await File.ReadAllTextAsync(ticketPath))!;
        if (!string.Equals(ticket.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(await ProductStateStore.HashAsync(ticket.Target), ticket.NewHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup target mismatch.");
        var backup = BackupPath(ticket, directory);
        if (File.Exists(backup) && await ProductStateStore.HashAsync(backup) == ticket.OldHash) File.Delete(backup);
        // The temporary process may still be leaving Main. Bounded retries, never kill a process.
        for (int n = 0; n < 50; n++)
        {
            try
            {
                foreach (var file in new[] { "SneakyFlow.exe", "ready", "proceed", "cancel", "error.txt", "work.json" })
                {
                    var path = Path.Combine(directory, file);
                    DownloadDeletion.ValidatePath(directory, path);
                    File.Delete(path);
                }
                Directory.Delete(directory, false);
                return ticket;
            }
            catch (IOException) { await Task.Delay(100); }
        }
        throw new IOException("The update is installed, but its temporary files are still locked: " + directory);
    }
    internal static async Task WaitForCommitAsync(string directory)
    {
        for (int n = 0; n < 200; n++)
        {
            if (File.Exists(Path.Combine(directory, "cancel"))) throw new IOException("Update handoff cancelled.");
            if (File.Exists(Path.Combine(directory, "proceed"))) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The original app did not authorize replacement.");
    }
    public static async Task<string> FailureMessageAsync(string ticketPath, string ticketHash)
    {
        var directory = ValidateDirectory(ticketPath);
        if (await ProductStateStore.HashAsync(ticketPath) != ticketHash) throw new IOException("Invalid error handoff.");
        var ticket = JsonSerializer.Deserialize<UpdateTicket>(await File.ReadAllTextAsync(ticketPath))!;
        if (!string.Equals(ticket.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
            await ProductStateStore.HashAsync(ticket.Target) != ticket.OldHash) throw new IOException("Unexpected original executable.");
        return await ReadErrorAsync(directory);
    }
}
