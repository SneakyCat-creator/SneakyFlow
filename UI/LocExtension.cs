using System.Windows.Data;
using System.Windows.Markup;

namespace SneakyFlow.UI;

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = L.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
