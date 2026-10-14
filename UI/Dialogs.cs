using System.Windows;
using System.Windows.Controls;

namespace SneakyFlow.UI;

public static class Dialogs
{
    public static bool Confirm(Window owner, string title, string message) => Choose(owner, title, message, null) == true;
    public static bool? ChooseRemoval(Window owner, string name) => Choose(owner, L.T("Удалить раздачу"), name,
        L.T("Удалить скачанные файлы без корзины"));
    private static bool? Choose(Window owner, string title, string message, string? deleteOption)
    {
        bool? choice = null;
        var dialog = new Window { Owner = owner, Title = title, Width = 560, SizeToContent = SizeToContent.Height,
            MinHeight = 220, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Style = (Style)Application.Current.FindResource("AppWindowStyle") };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,14) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0,0,0,18) });
        var keep = new RadioButton { Content = L.T("Только раздачу из SneakyFlow · файлы останутся"), IsChecked = true, Margin = new Thickness(0,0,0,12) };
        var remove = new RadioButton { Content = deleteOption, Margin = new Thickness(0,0,0,12) };
        if (deleteOption is not null) { panel.Children.Add(keep); panel.Children.Add(remove); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        var yes = new Button { Content = deleteOption is null ? L.T("Да, удалить программу") : L.T("Удалить"), MinWidth = 110, Margin = new Thickness(0,0,8,0) };
        yes.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        yes.Click += (_, _) => { choice = deleteOption is null || remove.IsChecked == true; dialog.Close(); };
        var cancel = new Button { Content = L.T("Отмена"), MinWidth = 100, IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(yes); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        dialog.Content = panel; dialog.ShowDialog(); return choice;
    }

    public static void ShowMessage(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Style = (Style)Application.Current.FindResource("AppWindowStyle"),
            Owner = owner, Title = title, Width = 540, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 8, 0) }
        });
        var close = new Button { Content = L.T("Понятно"), IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(close);
        dialog.Content = panel;
        dialog.ShowDialog();
    }
}
