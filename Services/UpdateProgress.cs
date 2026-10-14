namespace SneakyFlow.Services;

public enum UpdateStage { Idle, Checking, Downloading, Verifying, Waiting, Installing, UpToDate, Completed, Failed, Cancelled }

public sealed record UpdateProgress(UpdateStage Stage, int? Percent = null, string? Detail = null)
{
    public bool IsActive => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Waiting or UpdateStage.Installing;
}

/// <summary>Reports synchronously on the caller's context; no queued, stale Progress callbacks.</summary>
public sealed class UpdateActivity : IProgress<UpdateProgress>
{
    public UpdateProgress Current { get; private set; } = new(UpdateStage.Idle);
    public event Action<UpdateProgress>? Changed;
    public void Report(UpdateProgress value) { Current = value; Changed?.Invoke(value); }
}

internal sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
