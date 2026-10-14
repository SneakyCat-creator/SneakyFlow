using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SneakyFlow;
using SneakyFlow.Models;
using SneakyFlow.Services;
using SneakyFlow.UI;

internal static class UpdateUiChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Settle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static SettingsWindow Show(UpdateActivity activity, Func<SettingsWindow, Task> run)
    {
        var window = new SettingsWindow(new AppSettings(), "Isolated test profile", activity, run)
        { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        ((UpdateButton)window.FindName("UpdateAction")).BringIntoView();
        return window;
    }
    private static Button Button(SettingsWindow window) => (Button)((UpdateButton)window.FindName("UpdateAction")).FindName("ActionButton");
    private static void Click(SettingsWindow window) => Button(window).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static T Part<T>(SettingsWindow window, string name) where T : DependencyObject => (T)Button(window).Template.FindName(name, Button(window));

    public static async Task RunAsync(Action<Window, string> capture)
    {
        L.SetLanguage("ru"); UiScale.SetFactor(1);
        var activity = new UpdateActivity();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var window = Show(activity, async _ => { calls++; await finish.Task; activity.Report(new(UpdateStage.UpToDate)); });
        await Settle();
        var action = (UpdateButton)window.FindName("UpdateAction");
        var size = action.RenderSize;
        Click(window); Click(window); await Settle();
        Check(calls == 1 && window.IsVisible && !Button(window).IsEnabled, "Update click closed Settings or started twice.");
        Check(window.UpdateState.Current.Stage == UpdateStage.Checking, "Checking state missing.");
        capture(window, "update-checking-ru");
        foreach (var stage in new[] { UpdateStage.Downloading, UpdateStage.Installing })
        {
            foreach (int percent in new[] { 0, 1, 37, 99, 100 })
            {
                activity.Report(new(stage, percent)); await Settle();
                Check(action.RenderSize == size && action.ActualWidth == 260 && action.ActualHeight == 40, "Update button changed size.");
                Check(Part<ProgressBar>(window, "Fill").Value == percent && Part<TextBlock>(window, "Percent").Text == percent + "%", "Fill and text percentages diverged.");
                var fill = Part<ProgressBar>(window, "Fill");
                var indicator = (FrameworkElement)fill.Template.FindName("PART_Indicator", fill);
                Check(Math.Abs(indicator.ActualWidth - fill.ActualWidth * percent / 100) < 1.1, "Visible progress width does not match its percentage.");
                var label = Part<TextBlock>(window, "Label");
                Check(label.ActualWidth >= label.DesiredSize.Width - 0.1, "Update label was clipped.");
                if (percent == 37) capture(window, "update-" + stage.ToString().ToLowerInvariant() + "-ru");
            }
            activity.Report(new(stage, 5)); await Settle();
            Check(window.UpdateState.Percent == 100, "Progress moved backwards within a stage.");
        }
        window.Close();
        Check(window.IsVisible && !window.UpdateCancellation.IsCancellationRequested, "Settings closed in the middle of installation.");
        activity.Report(new(UpdateStage.Verifying)); await Settle();
        Check(!window.UpdateState.HasPercent && window.UpdateState.IsIndeterminate, "Verification fabricated a percentage.");
        finish.SetResult(); await Settle();
        Check(window.IsVisible && !Button(window).IsEnabled && window.UpdateState.IsSuccess, "Up-to-date state must stay visible and disabled.");
        Check(((SolidColorBrush)Part<Border>(window, "Surface").Background).Color == Color.FromRgb(0x25, 0x4B, 0x3C) && Button(window).Opacity == 1, "Success state is not readable green.");
        capture(window, "update-latest-ru");
        var latestLabel = Part<TextBlock>(window, "Label");
        Check(latestLabel.ActualWidth >= latestLabel.DesiredSize.Width - 0.1, "Latest-version label clipped.");
        Click(window); Check(calls == 1, "Finished check could be clicked again.");
        window.Close();

        var retry = Show(activity, _ => Task.FromException(new System.IO.IOException("Test connection error")));
        await Settle(); Check(Button(retry).IsEnabled, "Reopening Settings did not reset the button.");
        Click(retry); await Settle();
        Check(retry.IsVisible && Button(retry).IsEnabled && retry.UpdateState.IsError && ((TextBlock)retry.FindName("UpdateDetail")).Text.Contains("Test connection error"), "Failure is not visible/retryable in Settings.");
        var detail = (TextBlock)retry.FindName("UpdateDetail");
        DependencyObject ancestor = detail;
        while (ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
        var bounds = detail.TransformToAncestor((Visual)ancestor).TransformBounds(new Rect(detail.RenderSize));
        Check(bounds.Top >= 0 && bounds.Bottom <= ((ScrollViewer)ancestor).ActualHeight + 1, "Inline error is outside the visible viewport.");
        capture(retry, "update-error-ru");
        retry.Close();

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelWindow = Show(new(), async w =>
        {
            try { await Task.Delay(Timeout.Infinite, w.UpdateCancellation); }
            finally { cancelled.SetResult(); }
        });
        Click(cancelWindow); cancelWindow.Close();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(cancelWindow.UpdateCancellation.IsCancellationRequested, "Closing Settings did not cancel its pending check.");

        var draftWindow = Show(new(), _ => Task.CompletedTask);
        ((TextBox)draftWindow.FindName("MaxDownloadBox")).Text = "12,5";
        ((ComboBox)draftWindow.FindName("ScaleBox")).SelectedValue = "1.25";
        ((CheckBox)draftWindow.FindName("AdvancedLoggingBox")).IsChecked = true;
        var draft = draftWindow.CaptureDraft();
        draftWindow.ShowUpdateProgress(new(UpdateStage.Installing, 98));
        draftWindow.AllowUpdateRestart(); draftWindow.Close();
        Check(!draftWindow.UpdateCancellation.IsCancellationRequested, "Authorized restart cancelled the handoff.");
        var resumed = Show(new(), _ => Task.CompletedTask);
        resumed.RestoreDraft(draft); resumed.ShowUpdateProgress(new(UpdateStage.Completed, 100)); await Settle();
        Check(draft.All(pair => resumed.CaptureDraft()[pair.Key] == pair.Value), "Unsaved fields were lost across update restart.");
        Check(!Button(resumed).IsEnabled && resumed.UpdateState.PercentText == "100%", "Successful restart did not show completion.");
        Check(((ComboBox)resumed.FindName("ScaleBox")).Items.Cast<ComboBoxItem>().All(i => i.Content.ToString()!.EndsWith('x') && !i.Content.ToString()!.Contains('×')), "Scale labels do not use ordinary lowercase x.");
        capture(resumed, "update-completed-ru"); resumed.Close();

        foreach (var language in new[] { "ru", "en" })
        {
            L.SetLanguage(language);
            var about = new AboutWindow();
            Check(((CheckBox)about.FindName("AcceptedBox")).Content.ToString() == (language == "ru" ? "Соглашение принято" : "Agreement accepted"), "Agreement checkbox still has the removal suffix.");
            about.Close();
            UiScale.SetFactor(1.5);
            var zoom = Show(new(), _ => Task.CompletedTask);
            zoom.ShowUpdateProgress(new(UpdateStage.UpToDate)); await Settle();
            capture(zoom, "update-latest-150-" + language);
            Check(((UpdateButton)zoom.FindName("UpdateAction")).RenderSize == size, "Zoom changed the button's logical size.");
            zoom.ShowUpdateProgress(new(UpdateStage.Installing, 37)); await Settle();
            capture(zoom, "update-installing-150-" + language);
            zoom.AllowUpdateRestart(); zoom.Close();
        }
        UiScale.SetFactor(1); L.SetLanguage("ru");
        Console.WriteLine("PASS update button real clicks, fixed geometry, fill/percent, green completion, retry, close cancellation, restart draft, RU/EN and 150% scale");
    }
}
