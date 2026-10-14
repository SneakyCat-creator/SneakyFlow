using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace SneakyFlow.UI;

/// <summary>Application zoom, independent of the monitor's Windows DPI setting.</summary>
public static class UiScale
{
    private sealed record Target(WeakReference<Window> Window, double MinWidth, double MinHeight);
    private static readonly List<Target> Targets = [];
    public static double Factor { get; private set; } = 1;
    public static ScaleTransform Transform { get; } = new(1, 1);
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(UiScale), new PropertyMetadata(false, OnEnabled));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    public static void SetFactor(double value)
    {
        Factor = double.IsFinite(value) ? Math.Clamp(value, 1, 1.5) : 1;
        Transform.ScaleX = Transform.ScaleY = Factor;
        Targets.RemoveAll(t => !t.Window.TryGetTarget(out _));
        foreach (var target in Targets)
            if (target.Window.TryGetTarget(out var window)) Apply(window, target, false);
    }

    private static void OnEnabled(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window || args.NewValue is not true) return;
        window.Loaded += OnLoaded;
        window.Closed += OnClosed;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = (Window)sender;
        window.Loaded -= OnLoaded;
        var target = new Target(new(window), window.MinWidth, window.MinHeight);
        Targets.Add(target);
        if (window.Content is FrameworkElement content) content.LayoutTransform = Transform;
        Apply(window, target, true);
    }

    private static void OnClosed(object? sender, EventArgs e) =>
        Targets.RemoveAll(t => !t.Window.TryGetTarget(out var window) || ReferenceEquals(window, sender));

    private static void Apply(Window window, Target target, bool initial)
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
        var matrix = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var area = matrix.Transform(new Point(screen.WorkingArea.Width, screen.WorkingArea.Height));
        window.MinWidth = Math.Min(target.MinWidth * Factor, area.X);
        window.MinHeight = Math.Min(target.MinHeight * Factor, area.Y);
        if (window is MainWindow)
        {
            // The native caption hit area must follow the scaled custom title bar.
            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome is not null)
            {
                chrome.CaptionHeight = 60 * Factor;
                chrome.GlassFrameThickness = new Thickness(0, 60 * Factor, 0, 0);
            }
        }
        else if (initial)
        {
            if (double.IsFinite(window.Width)) window.Width = Math.Min(window.Width * Factor, area.X);
            if (double.IsFinite(window.Height)) window.Height = Math.Min(window.Height * Factor, area.Y);
            window.MaxHeight = area.Y;
        }
    }
}
