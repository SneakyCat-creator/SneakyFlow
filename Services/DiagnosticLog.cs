using System.IO;
using System.Threading.Channels;

namespace SneakyFlow.Services;

/// <summary>Bounded local history; disk access never runs on the WPF dispatcher.</summary>
internal sealed class DiagnosticLog : IAsyncDisposable
{
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;
    public DiagnosticLog(string directory) => _writer = Task.Run(() => WriteAsync(directory));
    public void Write(string text) => _lines.Writer.TryWrite($"{DateTimeOffset.Now:O} {text}");
    private async Task WriteAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "transfer.log");
            await foreach (var line in _lines.Reader.ReadAllAsync())
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                await File.AppendAllTextAsync(path, line + Environment.NewLine);
            }
        }
        catch (IOException) { } // Diagnostics must not interrupt transfers.
        catch (UnauthorizedAccessException) { }
    }
    public async ValueTask DisposeAsync() { _lines.Writer.TryComplete(); await _writer; }
}
