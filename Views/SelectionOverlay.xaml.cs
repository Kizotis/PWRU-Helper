using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace PWRUHelper;

/// <summary>
/// A full-screen transparent overlay. The user drags a rectangle; on release we
/// return that rectangle in PHYSICAL screen pixels. The corners are read straight
/// from the OS cursor position (GetCursorPos), which is already in physical pixels
/// for our per-monitor-DPI-aware process — so it stays correct even across monitors
/// with different scaling, where WPF's own PointToScreen would drift. Returns null
/// if cancelled with Esc.
/// </summary>
public partial class SelectionOverlay : Window
{
    private Point _startPoint;               // window coords, for drawing the marquee
    private System.Drawing.Point _startPhysical;  // physical screen pixels, for the result
    private bool _dragging;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static System.Drawing.Point PhysicalCursor()
        => GetCursorPos(out var p) ? new System.Drawing.Point(p.X, p.Y) : new System.Drawing.Point(0, 0);

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
        _startPhysical = PhysicalCursor();
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

        // Read the release corner straight from the OS in physical pixels (see class note).
        var p1 = _startPhysical;
        var p2 = PhysicalCursor();

        int x = Math.Min(p1.X, p2.X);
        int y = Math.Min(p1.Y, p2.Y);
        int w = Math.Abs(p2.X - p1.X);
        int h = Math.Abs(p2.Y - p1.Y);

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
