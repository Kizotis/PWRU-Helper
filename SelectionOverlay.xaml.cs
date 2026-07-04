using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace PWRUHelper;

/// <summary>
/// A full-screen transparent overlay. The user drags a rectangle; on release we
/// return that rectangle in PHYSICAL screen pixels (via PointToScreen, so it is
/// correct at any display scaling). Returns null if cancelled with Esc.
/// </summary>
public partial class SelectionOverlay : Window
{
    private Point _startPoint;
    private bool _dragging;

    /// <summary>Selected region in physical screen pixels, or null if cancelled.</summary>
    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    public SelectionOverlay()
    {
        InitializeComponent();

        // Cover the entire virtual desktop (all monitors), in WPF DIP units.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Park the hint near the top-centre.
        Loaded += (_, _) =>
        {
            Canvas.SetLeft(HintBox, Math.Max(20, (Width / 2) - 170));
            Canvas.SetTop(HintBox, 30);
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = null;
            DialogResult = false;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left) return;

        _startPoint = e.GetPosition(RootCanvas);
        _dragging = true;
        HintBox.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Visible;
        UpdateRect(_startPoint, _startPoint);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        UpdateRect(_startPoint, e.GetPosition(RootCanvas));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.ChangedButton != MouseButton.Left) return;

        _dragging = false;
        ReleaseMouseCapture();

        var endPoint = e.GetPosition(RootCanvas);

        // Convert both corners to physical screen pixels (DPI-correct).
        var p1 = RootCanvas.PointToScreen(_startPoint);
        var p2 = RootCanvas.PointToScreen(endPoint);

        int x = (int)Math.Round(Math.Min(p1.X, p2.X));
        int y = (int)Math.Round(Math.Min(p1.Y, p2.Y));
        int w = (int)Math.Round(Math.Abs(p2.X - p1.X));
        int h = (int)Math.Round(Math.Abs(p2.Y - p1.Y));

        if (w < 4 || h < 4)
        {
            // Treated as an accidental click — cancel.
            SelectedRegion = null;
            DialogResult = false;
        }
        else
        {
            SelectedRegion = new System.Drawing.Rectangle(x, y, w, h);
            DialogResult = true;
        }
        Close();
    }

    private void UpdateRect(Point a, Point b)
    {
        double left = Math.Min(a.X, b.X);
        double top = Math.Min(a.Y, b.Y);
        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = Math.Abs(a.X - b.X);
        SelectionRect.Height = Math.Abs(a.Y - b.Y);
    }
}
