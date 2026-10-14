using MonoTorrent.PortForwarding;

namespace SneakyFlow.Services;

/// <summary>Normal forwarding is unchanged. Router cleanup shares one deadline at application exit.</summary>
internal sealed class ShutdownPortForwarder(IPortForwarder inner) : IPortForwarder
{
    private CancellationTokenSource? _shutdown;
    public event EventHandler MappingsChanged { add => inner.MappingsChanged += value; remove => inner.MappingsChanged -= value; }
    public bool Active => inner.Active;
    public Mappings Mappings => inner.Mappings;
    public void BeginShutdown() => _shutdown ??= new CancellationTokenSource(TimeSpan.FromSeconds(1));
    public Task RegisterMappingAsync(Mapping mapping) => inner.RegisterMappingAsync(mapping);
    public Task StartAsync(CancellationToken token) => inner.StartAsync(token);
    public Task UnregisterMappingAsync(Mapping mapping, CancellationToken token) => CleanupAsync(t => inner.UnregisterMappingAsync(mapping, t), token);
    public Task StopAsync(CancellationToken token) => StopAsync(true, token);
    public Task StopAsync(bool removeExistingMappings, CancellationToken token) => CleanupAsync(t => inner.StopAsync(removeExistingMappings, t), token);

    private async Task CleanupAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        if (_shutdown is null) { await action(token); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        var pending = action(linked.Token);
        try { await pending.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Mono.Nat cannot cancel an in-flight HTTP request. Observe its eventual
            // completion without making disk flushing wait for an unreachable router.
            _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
