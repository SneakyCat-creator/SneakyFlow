using System.IO;
using System.Text.Json;

namespace SneakyFlow.UI;

public sealed record WindowLayout(double Width = 1280, double Height = 890, double? Left = null,
    double? Top = null, bool Maximized = false, double DetailsHeight = 340);

public sealed class WindowLayoutStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "window.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public WindowLayout? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var value = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_path));
            return value is not null && double.IsFinite(value.Width) && double.IsFinite(value.Height)
                && double.IsFinite(value.DetailsHeight) && value.Width > 0 && value.Height > 0
                && (value.Left is null || double.IsFinite(value.Left.Value))
                && (value.Top is null || double.IsFinite(value.Top.Value)) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task SaveAsync(WindowLayout layout)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await File.WriteAllTextAsync(_path + ".tmp", JsonSerializer.Serialize(layout));
                File.Move(_path + ".tmp", _path, true);
            });
        }
        finally { _gate.Release(); }
    }
}
