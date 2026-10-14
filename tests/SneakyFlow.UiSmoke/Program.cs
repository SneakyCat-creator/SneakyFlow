using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using SneakyFlow;
using SneakyFlow.Models;
using SneakyFlow.Services;
using SneakyFlow.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/ui-check");
            Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source = XDocument.Load("App.xaml");
            var dictionary = new XElement(wpf + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                new XAttribute(XNamespace.Xmlns + "ui", "clr-namespace:SneakyFlow.UI;assembly=SneakyFlow"),
                source.Root!.Element(wpf + "Application.Resources")!.Elements());
            app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            Pump(BrandMigrationChecks.RunAsync(output));
            Pump(MaintenanceChecks.RunAsync(output));
            Pump(UpdateUiChecks.RunAsync((window, name) => Capture(window, name, output, false)));
            if (args.Contains("--native-menu")) return RunNativeMenuHarness(app);
            var service = new TorrentService(Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N")));
            var fixture = new TorrentItem();
            Set(fixture, "Name", "Проверка интерфейса · тестовая раздача");
            Set(fixture, "Size", 1_073_741_824L);
            Set(fixture, "Downloaded", 268_435_456L);
            Set(fixture, "Progress", 25d);
            Set(fixture, "HasMetadata", true);
            Set(fixture, "State", TorrentStatus.Downloading);
            Set(fixture, "Status", "Ожидание пиров");
            Set(fixture, "Diagnosis", "Адреса пиров известны (12), подключённых нет. Участники могут быть недоступны или отклонять соединение.");
            Set(fixture, "ConnectionSummary", "Подключено 0: сидов 0, пиров 0 · известных адресов 12 · передают 0");
            Set(fixture, "KnownPeers", 12);
            Set(fixture, "TrackerSeeds", 32);
            Set(fixture, "TrackerLeechers", 7);
            Set(fixture, "PieceCount", 320);
            Set(fixture, "CompletedPieces", 80);
            Set(fixture, "Remaining", 805_306_368L);
            Set(fixture, "PieceMap", Enumerable.Range(0, 320).Select(i => (byte)(i < 60 || (i >= 100 && i < 120) ? 1 : 0)).ToArray());
            Set(fixture, "DhtStatus", "DHT: подключение к сети · узлов 0");
            Set(fixture, "TrackerSummary", "Трекеры: отвечают 1 из 2");
            Set(fixture, "NetworkStatus", "TCP IPv4/IPv6 · тестовый профиль");
            Set(fixture, "ElapsedText", "3 мин 12 с");
            Set(fixture, "LastActivity", "Данные не поступают 32 с");
            Set(fixture, "EtaText", "12 мин 34 с");
            Set(fixture, "DownloadSpeed", 26_500_000L);
            Set(fixture, "DiskStatus", "Диск: запись 26,5 МБ/с · ожидает записи 0,1 МБ");
            Set(fixture, "TransferStatus", "Получено за сеанс 280 МБ; прогресс учитывает проверенные сегменты.");
            fixture.PeerDetails.Add(new PeerItem("127.0.0.1:6881", "Тестовый клиент", "Сид", "Передаёт данные", 26_500_000, 0, 120));
            Set(fixture, "SavePath", output);
            Set(fixture, "InfoHash", ""); // Read-only computed property is deliberately skipped by Set.
            fixture.Trackers.Add(new TrackerItem { Address = "http://127.0.0.1:1234/announce" });
            fixture.Events.Add(new TorrentEventItem(DateTimeOffset.Now, "Тестовый профиль — пользовательские раздачи не используются"));
            service.Torrents.Add(fixture);
            if (DisplayFormat.Speed(1_000_000) != "1 МБ/с" || !DisplayFormat.Speed(1024 * 1024).EndsWith("МБ/с")) throw new Exception("Decimal MB/s format is incorrect.");
            var mainWindow = new MainWindow(service);
            mainWindow.Show();
            mainWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (!args.Contains("--headless-caption")) CheckNativeCaption(mainWindow);
            var detailsTabs = (TabControl)mainWindow.FindName("DetailsTabs");
            if (((FrameworkElement)detailsTabs.Template.FindName("Headers", detailsTabs)).Visibility != Visibility.Collapsed ||
                ((TabItem)mainWindow.FindName("PeersTab")).Visibility != Visibility.Collapsed) throw new Exception("Default diagnostics headers must be hidden.");
            service.Settings.AdvancedLogging = true;
            typeof(MainWindow).GetMethod("RefreshUi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mainWindow,null);
            if (((FrameworkElement)detailsTabs.Template.FindName("Headers", detailsTabs)).Visibility != Visibility.Visible ||
                ((TabItem)mainWindow.FindName("PeersTab")).Visibility != Visibility.Visible) throw new Exception("Live diagnostics headers did not appear.");
            service.Settings.AdvancedLogging = false;
            typeof(MainWindow).GetMethod("RefreshUi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mainWindow,null);
            if (((TextBlock)mainWindow.FindName("RemainingTime")).Text != "12 мин 34 с") throw new Exception("Selected ETA missing");
            var second = new TorrentItem();
            Set(second, "Name", "Другая раздача");
            Set(second, "EtaText", "2 ч 10 мин");
            service.Torrents.Add(second);
            ((ListBox)mainWindow.FindName("TorrentList")).SelectedItem = second;
            if (((TextBlock)mainWindow.FindName("RemainingTime")).Text != "2 ч 10 мин") throw new Exception("ETA did not follow selection");
            ((ListBox)mainWindow.FindName("TorrentList")).SelectedItem = fixture;
            if (((TextBlock)mainWindow.FindName("SelectedSize")).Text != "1,1") throw new Exception("Selected decimal size missing");
            if (mainWindow.FontSize != 13) throw new Exception("Default font size is not the original 1x baseline");
            foreach (var rate in new[] { 0, 1, 512, 20000, 2097151 })
                if (RateLimitInput.Parse(RateLimitInput.Format(rate), "test") != rate) throw new Exception("Existing speed limit changed during MB/s conversion");
            if (DisplayFormat.Bytes(1_000_000_000) != "1 ГБ") throw new Exception("Decimal GB format failed");
            CheckNativeMenu();
            var units = (TextBlock)mainWindow.FindName("DownloadUnit");
            var number = (TextBlock)mainWindow.FindName("DownloadRate");
            var unitsX = units.TransformToAncestor(mainWindow).Transform(new Point()).X;
            number.Text = "100000";
            mainWindow.UpdateLayout();
            if (Math.Abs(units.TransformToAncestor(mainWindow).Transform(new Point()).X - unitsX) > 0.1) throw new Exception("Speed units move with number width");
            number.Text = "26,5";
            Capture(mainWindow, "main", output, false);
            mainWindow.IsEnabled = false;
            Capture(mainWindow, "main-disabled", output, false);
            mainWindow.IsEnabled = true;
            Capture(new SettingsWindow(new AppSettings(), "Профиль Windows / SneakyFlow"), "settings", output);
            Capture(new AddTorrentWindow(), "add-torrent", output);
            Capture(new AboutWindow(), "about", output);
            Capture(new AboutWindow(true), "agreement", output);

            L.SetLanguage("en");
            typeof(MainWindow).GetMethod("RefreshUi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mainWindow, null);
            mainWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (!((Button)mainWindow.FindName("AddButton")).Content.ToString()!.Contains("Add torrent")) throw new Exception("Live English binding failed");
            if (DisplayFormat.Speed(45_500_000) != "45.5 MB/s") throw new Exception("English speed format failed");
            if (((TextBlock)mainWindow.FindName("SelectedSizeUnit")).Text != "GB") throw new Exception("English unit failed");
            Set(fixture, "Name", "UI check · synthetic torrent");
            Set(fixture, "Status", "Downloading");
            Set(fixture, "Diagnosis", L.T("Данные поступают. Части проверяются и записываются на диск."));
            Set(fixture, "EtaSeconds", (double?)754);
            typeof(MainWindow).GetMethod("RefreshUi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mainWindow, null);
            if (((TextBlock)mainWindow.FindName("RemainingTime")).Text != "00:12:34") throw new Exception("Stable ETA clock failed");
            Capture(mainWindow, "main-en", output, false);
            var englishSettings = new SettingsWindow(new AppSettings { Language = "en" }, "Windows profile / SneakyFlow");
            Capture(englishSettings, "settings-en", output, false);
            var scroll = Descendants<ScrollViewer>(englishSettings).First();
            scroll.ScrollToEnd();
            Capture(englishSettings, "settings-en-bottom", output);
            Capture(new AddTorrentWindow(), "add-torrent-en", output);
            Capture(new AboutWindow(), "about-en", output);
            foreach (var factor in new[] { 1.1, 1.25, 1.5 })
            {
                UiScale.SetFactor(factor);
                mainWindow.UpdateLayout();
                var root = (FrameworkElement)mainWindow.Content;
                if (Math.Abs(((ScaleTransform)root.LayoutTransform).ScaleX - factor) > 0.001) throw new Exception("UI zoom missing");
                if (root.ActualWidth * factor > mainWindow.ActualWidth + 2 || root.ActualHeight * factor > mainWindow.ActualHeight + 2) throw new Exception("Zoomed main content exceeds window bounds");
                Capture(mainWindow, "main-scale-" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture), output, false);
                var zoomSettings = new SettingsWindow(new AppSettings { UiScale = factor }, "Windows profile / SneakyFlow");
                Capture(zoomSettings, "settings-scale-" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture), output);
            }
            Capture(new AddTorrentWindow(), "add-torrent-scale-150", output);
            Capture(new AboutWindow(), "about-scale-150", output);
            UiScale.SetFactor(1);

            // CI's virtual desktop can be smaller than the normal minimum window size.
            // Exercise persistence at a real, feasible size instead of asserting a size Windows rejected.
            var headless = args.Contains("--headless-caption");
            var workArea = SystemParameters.WorkArea;
            var testWidth = headless ? Math.Min(1340, workArea.Width - 60) : 1340;
            var testHeight = headless ? Math.Min(930, workArea.Height - 70) : 930;
            if (headless) { mainWindow.MinWidth = 700; mainWindow.MinHeight = 600; }
            mainWindow.Left = workArea.Left + 30; mainWindow.Top = workArea.Top + 35;
            mainWindow.Width = testWidth; mainWindow.Height = testHeight;
            mainWindow.UpdateLayout();
            mainWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var expectedWidth = mainWindow.ActualWidth;
            var expectedHeight = mainWindow.ActualHeight;
            var contentGrid = (Grid)mainWindow.FindName("ContentGrid");
            var availableDetails = contentGrid.ActualHeight - contentGrid.RowDefinitions.Take(4).Sum(r => r.ActualHeight) - 136;
            var expectedDetails = Math.Clamp(headless ? 270 : 410, 240, Math.Max(240, availableDetails));
            ((RowDefinition)mainWindow.FindName("DetailsRow")).Height = new GridLength(expectedDetails);
            mainWindow.UpdateLayout();
            var splitter = (System.Windows.Controls.GridSplitter)mainWindow.FindName("DetailsSplitter");
            splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false));
            Pump(mainWindow.PersistLayoutAsync());
            var savedLayout = new WindowLayoutStore(service.DataDirectory).Load()!;
            if (Math.Abs(savedLayout.Width - expectedWidth) > 1 || Math.Abs(savedLayout.Height - expectedHeight) > 1 || Math.Abs(savedLayout.DetailsHeight - expectedDetails) > 1) throw new Exception($"Layout was not saved: expected {expectedWidth}x{expectedHeight}/{expectedDetails}, got {savedLayout}");
            mainWindow.Close();
            var restored = new MainWindow(service);
            if (headless) { restored.MinWidth = 700; restored.MinHeight = 600; }
            restored.Show(); restored.UpdateLayout();
            restored.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (Math.Abs(restored.ActualWidth - expectedWidth) > 1 || Math.Abs(restored.ActualHeight - expectedHeight) > 1 || Math.Abs(((RowDefinition)restored.FindName("DetailsRow")).ActualHeight - expectedDetails) > 1) throw new Exception("Window or panel layout not restored");
            restored.WindowState = WindowState.Maximized;
            restored.UpdateLayout();
            restored.WindowState = WindowState.Minimized;
            Pump(restored.PersistLayoutAsync());
            restored.Close();
            var maximized = new MainWindow(service);
            if (headless) { maximized.MinWidth = 700; maximized.MinHeight = 600; }
            maximized.Show();
            if (maximized.WindowState != WindowState.Maximized) throw new Exception("Maximized state not restored");
            SendMessage(new System.Windows.Interop.WindowInteropHelper(maximized).Handle, 0x112, new IntPtr(0xf060), IntPtr.Zero);
            if (maximized.IsVisible) throw new Exception("Native close command failed");
            L.SetLanguage("ru");
            Console.WriteLine("PASS native HMENU, 1x fonts, decimal byte units, lossless saved rate limits, UI scales 100/110/125/150%, live languages, window/panel/maximized persistence");
            Console.WriteLine("PASS dark content surfaces, uncovered native caption, native close command; screenshots rendered");
            app.Shutdown();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int RunNativeMenuHarness(Application app)
    {
        var type = typeof(TrayService).Assembly.GetType("SneakyFlow.Services.NativeTrayMenu")!;
        using var menu = (IDisposable)Activator.CreateInstance(type)!;
        var window = new Window { Title = "SneakyFlow — проверка системного меню", Width = 500, Height = 210, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var status = new TextBlock { Text = "Тестовое меню Windows · профиль не используется", Margin = new Thickness(0,0,0,18) };
        var open = new Button { Content = "Открыть системное меню" };
        open.Click += (_, _) =>
        {
            var result = (uint)type.GetMethod("Show")!.Invoke(menu, null)!;
            status.Text = result == 1 ? "Открыть: OK" : result == 2 ? "Выход: OK" : "Отмена: OK";
            Console.WriteLine(status.Text);
            if (result == 2) { window.Close(); app.Shutdown(); }
        };
        panel.Children.Add(status); panel.Children.Add(open); window.Content = panel;
        window.Closed += (_, _) => app.Shutdown();
        return app.Run(window);
    }

    private static void CheckNativeMenu()
    {
        var type = typeof(TrayService).Assembly.GetType("SneakyFlow.Services.NativeTrayMenu")!;
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var menu = (IntPtr)type.GetMethod("CreateMenu", flags)!.Invoke(null, null)!;
        try
        {
            if (GetMenuItemCount(menu) != 3 || GetMenuItemID(menu, 0) != 1 || GetMenuItemID(menu, 2) != 2) throw new Exception("Native tray commands missing");
            // MF_OWNERDRAW and MF_BITMAP must not be set: Windows renders plain native items.
            if ((GetMenuState(menu, 0, 0x400) & (0x100 | 0x4)) != 0 || (GetMenuState(menu, 1, 0x400) & 0x800) == 0) throw new Exception("Tray menu is not a standard system menu");
        }
        finally { type.GetMethod("DestroyMenu", flags)!.Invoke(null, [menu]); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    private static void CheckNativeCaption(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(handle, 5, out var buttons, 16) != 0 || buttons.Right <= buttons.Left)
            throw new Exception("Windows caption buttons are unavailable");
        GetWindowRect(handle, out var bounds);
        int[] expected = [8, 9, 20]; // HTMINBUTTON, HTMAXBUTTON, HTCLOSE.
        for (int i = 0; i < 3; i++)
        {
            var x = bounds.Left + buttons.Left + (buttons.Right - buttons.Left) * (2 * i + 1) / 6;
            var y = bounds.Top + (buttons.Top + buttons.Bottom) / 2;
            var point = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
            if (SendMessage(handle, 0x84, IntPtr.Zero, point).ToInt64() != expected[i])
                throw new Exception($"Native caption hit-testing failed at button {i}");
        }
        SendMessage(handle, 0x112, new IntPtr(0xf030), IntPtr.Zero);
        if (window.WindowState != WindowState.Maximized) throw new Exception("Native maximize command failed");
        SendMessage(handle, 0x112, new IntPtr(0xf120), IntPtr.Zero);
        if (window.WindowState != WindowState.Normal) throw new Exception("Native restore from maximized failed");
        SendMessage(handle, 0x112, new IntPtr(0xf020), IntPtr.Zero);
        if (window.WindowState != WindowState.Minimized) throw new Exception("Native minimize command failed");
        SendMessage(handle, 0x112, new IntPtr(0xf120), IntPtr.Zero);
        window.UpdateLayout();
        if (window.WindowState != WindowState.Normal) throw new Exception("Native restore command failed");
        Console.WriteLine("PASS native caption hit targets and maximize/minimize/restore commands");
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect bounds, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void Set(object model, string name, object value)
    {
        var property = model.GetType().GetProperty(name)!;
        if (property.SetMethod is not null) property.SetValue(model, value);
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Capture(Window window, string name, string output, bool close = true)
    {
        window.Left = -4000;
        window.Top = -4000;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var canvas = (SolidColorBrush)window.FindResource("CanvasBrush");
        if (canvas.Color != Color.FromRgb(0x17, 0x1d, 0x24) ||
            (window is not MainWindow && (window.Background is not SolidColorBrush brush || brush.Color != canvas.Color)))
            throw new InvalidOperationException($"{name}: incorrect background {window.Background}");
        if (window is MainWindow && (window.Background is not SolidColorBrush nativeBackground || nativeBackground.Color.A != 0 || System.Windows.Shell.WindowChrome.GetWindowChrome(window)?.UseAeroCaptionButtons != true))
            throw new InvalidOperationException("Native caption must be uncovered by WPF's background");
        if (!WindowTheme.GetUseDarkTitleBar(window)) throw new InvalidOperationException($"{name}: dark title bar missing");
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(root.ActualWidth * UiScale.Factor);
        var height = (int)Math.Ceiling(root.ActualHeight * UiScale.Factor);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(canvas, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var whites = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 240 && pixels[i + 1] > 240 && pixels[i + 2] > 240) whites++;
        if ((double)whites / (width * height) > 0.005) throw new InvalidOperationException($"{name}: white surface detected ({whites} pixels)");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png"));
        png.Save(file);
        Console.WriteLine($"PASS {name}: {width}x{height}, white pixels={whites}");
        if (close) window.Close();
    }
}
