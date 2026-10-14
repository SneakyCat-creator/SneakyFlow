using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SneakyFlow.Services;

public static class SelfRemoval
{
    public static async Task PrepareAsync(ProductStateStore store)
    {
        if (!string.Equals(store.Root, AppStorage.DefaultProfileDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Self-removal is only available for the normal user profile.");
        var state = await store.ReadAsync();
        var process = Process.GetCurrentProcess();
        var current = Environment.ProcessPath ?? throw new IOException("Unknown executable path.");
        if (FileVersionInfo.GetVersionInfo(current).OriginalFilename != "SneakyFlow.dll")
            throw new IOException("Self-removal is unavailable in a development host.");
        await store.RegisterCopyAsync(current);
        state = await store.ReadAsync();
        var directory = UpdateHandoff.CreateDirectory();
        var path = Path.Combine(directory, "remove.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks,
            Current = current, Profile = store.Root, Copies = state.Copies, RuntimeCaches = state.RuntimeCaches
        }));
        using var resource = typeof(SelfRemoval).Assembly.GetManifestResourceStream("SneakyFlow.SelfRemove.ps1")!;
        using var reader = new StreamReader(resource);
        var script = await reader.ReadToEndAsync();
        // Plain, readable script; no cmd, encoded commands, policy changes, elevation or persistence.
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("& {\n" + script + "\n} '" + path.Replace("'", "''") + "' '" + await ProductStateStore.HashAsync(path) + "'");
        using var helper = Process.Start(start) ?? throw new IOException("Could not start Windows PowerShell.");
        for (int n = 0; n < 150; n++)
        {
            if (File.Exists(Path.Combine(directory, "remove-ready"))) return;
            if (helper.HasExited) throw new IOException(File.Exists(Path.Combine(directory, "remove-error.txt"))
                ? await File.ReadAllTextAsync(Path.Combine(directory, "remove-error.txt")) : "Windows blocked the removal helper.");
            await Task.Delay(100);
        }
        throw new TimeoutException("The removal helper did not confirm readiness. No program files have been deleted.");
    }
}
