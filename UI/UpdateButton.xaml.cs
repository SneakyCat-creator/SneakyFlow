using System.Windows;
using System.Windows.Controls;

namespace SneakyFlow.UI;

public partial class UpdateButton : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(UpdateButtonState), typeof(UpdateButton));
    public UpdateButtonState State { get => (UpdateButtonState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public event RoutedEventHandler? Click;
    public UpdateButton() { InitializeComponent(); }
    private void Action_Click(object sender, RoutedEventArgs e) { if (State.CanClick) Click?.Invoke(this, e); }
}
