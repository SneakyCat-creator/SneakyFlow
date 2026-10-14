using System.ComponentModel;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SneakyFlow.Services;

/// <summary>A real HMENU, drawn and operated entirely by Windows.</summary>
internal sealed class NativeTrayMenu : Forms.NativeWindow, IDisposable
{
    internal const uint Open = 1, Exit = 2;
    private bool _showing, _disposed;

    public NativeTrayMenu() => CreateHandle(new Forms.CreateParams
    {
        Caption = "SneakyFlow.TrayMenu", Style = unchecked((int)0x80000000), ExStyle = 0x80
    });

    internal static IntPtr CreateMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception();
        if (!AppendMenu(menu, 0, Open, L.T("Открыть SneakyFlow")) ||
            !AppendMenu(menu, 0x800, 0, null) || !AppendMenu(menu, 0, Exit, L.T("Выход")))
        {
            var error = new Win32Exception();
            DestroyMenu(menu);
            throw error;
        }
        SetMenuDefaultItem(menu, Open, false);
        return menu;
    }

    public uint Show()
    {
        if (_disposed || _showing || !GetCursorPos(out var point)) return 0;
        var menu = CreateMenu();
        _showing = true;
        try
        {
            SetForegroundWindow(Handle);
            // Respect the user's handedness preference. Windows handles screen edges.
            uint flags = 0x100 | 0x2 | (GetSystemMetrics(40) != 0 ? 0x8u : 0u);
            return TrackPopupMenuEx(menu, flags, point.X, point.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);
            _showing = false;
            if (_disposed) DestroyHandle();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_showing) EndMenu();
        else DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndMenu();
}
