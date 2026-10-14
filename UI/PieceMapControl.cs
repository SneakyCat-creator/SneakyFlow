using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SneakyFlow.UI;

/// <summary>Verified piece bitfield, grouped to screen pixels without inventing progress.</summary>
public sealed class PieceMapControl : FrameworkElement
{
    public static readonly DependencyProperty PiecesProperty = DependencyProperty.Register(
        nameof(Pieces), typeof(object), typeof(PieceMapControl),
        new FrameworkPropertyMetadata(Array.Empty<byte>(), FrameworkPropertyMetadataOptions.AffectsRender,
            (target, _) => ((PieceMapControl)target)._hoveredGroup = -1));

    private static readonly Brush MissingBrush = MakeBrush(0x39, 0x47, 0x50);
    private static readonly Brush CompleteBrush = MakeBrush(0x7D, 0xC5, 0xAB);
    private static readonly Brush MixedBrush = MakeBrush(0x56, 0x8C, 0x7B);
    private int _hoveredGroup = -1;

    // Use object for the public DP: WPF's BAML compiler treats a byte[] CLR property
    // as an inline array inside a DataTemplate, instead of preserving its Binding.
    public object Pieces { get => GetValue(PiecesProperty); set => SetValue(PiecesProperty, value); }
    private byte[] PieceData => Pieces as byte[] ?? Array.Empty<byte>();

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private int GroupCount => Math.Min(PieceData.Length, Math.Max(1, (int)(ActualWidth / 4)));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var pieces = PieceData;
        var groups = GroupCount;
        if (pieces is null || groups == 0)
        {
            dc.DrawRoundedRectangle(MissingBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 2, 2);
            ToolTip = L.T("Карта появится после получения метаданных торрента.");
            return;
        }
        for (var group = 0; group < groups; group++)
        {
            var first = (int)((long)group * pieces.Length / groups);
            var end = (int)((long)(group + 1) * pieces.Length / groups);
            var complete = 0;
            for (var i = first; i < end; i++) if (pieces[i] == 1) complete++;
            var x = group * ActualWidth / groups;
            var right = (group + 1) * ActualWidth / groups;
            var brush = complete == 0 ? MissingBrush : complete == end - first ? CompleteBrush : MixedBrush;
            dc.DrawRectangle(brush, null, new Rect(x, 0, Math.Max(1, right - x - 1), ActualHeight));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var groups = GroupCount;
        if (groups == 0 || ActualWidth <= 0) return;
        var group = Math.Clamp((int)(e.GetPosition(this).X / ActualWidth * groups), 0, groups - 1);
        if (_hoveredGroup == group && ToolTip is not null) return;
        _hoveredGroup = group;
        var pieces = PieceData;
        var first = (int)((long)group * pieces.Length / groups);
        var end = (int)((long)(group + 1) * pieces.Length / groups);
        var complete = 0;
        for (var i = first; i < end; i++) if (pieces[i] == 1) complete++;
        ToolTip = end - first == 1
            ? L.F($"Сегмент {first + 1:N0} / {pieces.Length:N0}: {(complete == 1 ? L.T("загружен и проверен") : L.T("ещё не загружен"))}.")
            : L.F($"Сегменты {first + 1:N0}–{end:N0} / {pieces.Length:N0}\nЗагружено и проверено: {complete:N0} из {end - first:N0}.\nОдин прямоугольник объединяет несколько сегментов.");
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoveredGroup = -1;
        base.OnMouseLeave(e);
    }
}
