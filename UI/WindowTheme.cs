using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SneakyFlow.UI;

/// <summary>Matches native Windows title bars to the application's dark surfaces.</summary>
public static class WindowTheme
{
    public static readonly DependencyProperty UseDarkTitleBarProperty = DependencyProperty.RegisterAttached(
        "UseDarkTitleBar", typeof(bool), typeof(WindowTheme), new PropertyMetadata(false, OnChanged));

    public static bool GetUseDarkTitleBar(DependencyObject target) => (bool)target.GetValue(UseDarkTitleBarProperty);
    public static void SetUseDarkTitleBar(DependencyObject target, bool value) => target.SetValue(UseDarkTitleBarProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        if (args.NewValue is true)
        {
            window.SourceInitialized += OnSourceInitialized;
            Apply(window);
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is Window window) Apply(window);
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = 1;
        // Unsupported attributes return HRESULT; older Windows safely keep their normal chrome.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        var caption = 0x00241D17; // COLORREF: #171D24.
        var text = 0x00C7CEC9;    // COLORREF: #C9CEC7.
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
