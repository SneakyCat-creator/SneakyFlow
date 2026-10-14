using SneakyFlow.Models;
using SneakyFlow.Services;

namespace SneakyFlow.UI;

public sealed class UpdateButtonState : ObservableModel
{
    public UpdateProgress Current { get; private set; } = new(UpdateStage.Idle);
    public bool CanClick => Current.Stage is UpdateStage.Idle or UpdateStage.Failed or UpdateStage.Cancelled;
    public bool IsSuccess => Current.Stage is UpdateStage.UpToDate or UpdateStage.Completed;
    public bool IsError => Current.Stage == UpdateStage.Failed;
    public bool IsInstalling => Current.Stage == UpdateStage.Installing;
    public bool IsIndeterminate => Current.Stage is UpdateStage.Checking or UpdateStage.Verifying or UpdateStage.Waiting;
    public bool HasPercent => Current.Percent.HasValue;
    public int Percent => Math.Clamp(Current.Percent ?? 0, 0, 100);
    public string PercentText => Percent + "%";
    public string? Detail => Current.Detail;
    public string Label => L.T(Current.Stage switch
    {
        UpdateStage.Checking => "Проверка обновления",
        UpdateStage.Downloading => "Загрузка",
        UpdateStage.Verifying => "Проверка файла",
        UpdateStage.Waiting => "Ожидание установки",
        UpdateStage.Installing => "Установка",
        UpdateStage.UpToDate => "Установлена последняя версия",
        UpdateStage.Completed => "Обновление установлено",
        UpdateStage.Failed => "Повторить проверку",
        UpdateStage.Cancelled => "Проверить обновления сейчас",
        _ => "Проверить обновления сейчас"
    });
    public string AccessibleName => Label + (HasPercent ? ", " + PercentText : "");

    public void Apply(UpdateProgress value)
    {
        if (Current.Stage == value.Stage && Current.Percent is { } previous && value.Percent is { } next)
            value = value with { Percent = Math.Max(previous, next) };
        Current = value;
        // Every label and the fill use the same snapshot and integer percentage.
        Notify("");
    }
}
