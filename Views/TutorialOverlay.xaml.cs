using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PWRUHelper.Services;

namespace PWRUHelper;

/// <summary>
/// The first-run tour's layer: dims the real main window, cuts a spotlight around the real control,
/// and has the guide (Kizotis) explain it. The steps and their words are <see cref="TutorialScript"/>'s; this
/// class only shows them.
///
/// <para><b>Nothing may keep running once it closes</b> — the game runs underneath. Every animation
/// uses <see cref="FillBehavior.Stop"/> over a base value that already holds its end state, so a
/// finished animation leaves no clock behind; the halo pulses a bounded number of times; there is no
/// idle loop; and <see cref="StopAll"/> clears whatever is still mid-flight on close.</para>
/// </summary>
public partial class TutorialOverlay : UserControl
{
    private const double SoftDim = 0.6;              // a lighter dim for the finish (≈47% instead of 78%)
    private static readonly Dictionary<string, BitmapImage> Poses = new();

    private IReadOnlyList<TutorialStep> _steps = Array.Empty<TutorialStep>();
    private Func<string, FrameworkElement?> _resolve = _ => null;
    private Action<int> _selectTab = _ => { };
    private Action<bool>? _onClosed;
    private int _index, _direction = 1;
    private int _generation;          // bumped per step and on close: a stale async step bails out
    private Rect _hole;               // where the spotlight rests (the end of any hole animation)
    private Rect? _target;            // the current step's measured target, or null

    public TutorialOverlay() => InitializeComponent();

    internal bool IsActive { get; private set; }
    internal int StepIndex => _index;
    internal int StepCount => _steps.Count;

    /// <param name="resolve">x:Name → the real control in the main window.</param>
    /// <param name="onClosed">Called once, as soon as the tour ends: true when finished with the last
    /// button, false when skipped or closed from outside.</param>
    internal void Start(IReadOnlyList<TutorialStep> steps, Func<string, FrameworkElement?> resolve,
                        Action<int> selectTab, Action<bool> onClosed)
    {
        if (IsActive || steps.Count == 0) return;
        _steps = steps;
        _resolve = resolve;
        _selectTab = selectTab;
        _onClosed = onClosed;
        _index = 0;
        _direction = 1;
        IsActive = true;

        Visibility = Visibility.Visible;
        UpdateLayout();
        FullRect.Rect = new Rect(Root.RenderSize);
        _hole = new Rect(Root.ActualWidth / 2, Root.ActualHeight / 2, 0, 0);
        Hole.Rect = _hole;
        Dim.Opacity = 1;
        Animate(this, OpacityProperty, 0, 1, 250, new CubicEase { EasingMode = EasingMode.EaseOut });
        _ = ShowStepAsync(0, entrance: true);
    }

    /// <summary>End the tour now. Safe to call when it is not running.</summary>
    internal void Close(bool finished)
    {
        if (!IsActive) return;
        IsActive = false;
        _generation++;
        var callback = _onClosed;
        _onClosed = null;
        callback?.Invoke(finished);

        Animate(this, OpacityProperty, Opacity, 0, 180);
        _ = CollapseAfterAsync(_generation);
    }

    private async Task CollapseAfterAsync(int generation)
    {
        await Task.Delay(180);
        if (generation != _generation || IsActive) return;
        StopAll();
        Visibility = Visibility.Collapsed;
    }

    internal void Next()
    {
        if (!IsActive) return;
        if (_index >= _steps.Count - 1) { Close(finished: true); return; }
        _direction = 1;
        _ = ShowStepAsync(_index + 1, entrance: false);
    }

    internal void Back()
    {
        if (!IsActive || _index == 0) return;
        _direction = -1;
        _ = ShowStepAsync(_index - 1, entrance: false);
    }

    /// <summary>The window routes its PreviewKeyDown here while the tour runs: → / Enter next,
    /// ← back, Esc skip. Every other key is swallowed too, so nothing reaches the controls behind
    /// the dim — except Tab, which only cycles the bubble's own buttons.</summary>
    internal void HandleKey(KeyEventArgs e)
    {
        if (!IsActive) return;
        switch (e.Key)
        {
            case Key.Right: Next(); break;
            case Key.Enter:
            case Key.Space:
                // A focused Back/Skip button keeps its own meaning; otherwise Enter means Next.
                if (Keyboard.FocusedElement is Button b && b != NextButton && Bubble.IsAncestorOf(b)) return;
                Next(); break;
            case Key.Left: Back(); break;
            case Key.Escape: Close(finished: false); break;
            case Key.Tab: return;
        }
        e.Handled = true;
    }

    // ---- one step ---------------------------------------------------------------------------

    private async Task ShowStepAsync(int index, bool entrance)
    {
        int gen = ++_generation;
        var step = _steps[index];
        _index = index;

        if (!entrance)
        {
            StopHalo();
            Animate(Bubble, OpacityProperty, Bubble.Opacity, 0, 120);
            Animate(Guide, OpacityProperty, Guide.Opacity, 0, 120);
            await Task.Delay(120);
            if (gen != _generation) return;
        }

        if (step.Tab is int tab) _selectTab(tab);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var control = step.Target is null ? null : _resolve(step.Target);
        if (control is { IsVisible: true })
        {
            control.BringIntoView();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }
        if (gen != _generation) return;

        _target = MeasureTarget(control, step.Padding);
        if (step.Target != null && _target is null)
        {
            // The control is not there (collapsed, zero size): skip the stop in the direction of travel.
            int next = index + _direction;
            if (next >= 0 && next < _steps.Count) await ShowStepAsync(next, entrance: false);
            else if (next >= _steps.Count) Close(finished: true);
            return;
        }

        FillBubble(step);
        var from = _hole;
        LayoutStep(step);                       // sets the resting _hole and every position
        if (from.Width <= 0 && _hole.Width > 0)
            from = new Rect(_hole.X + _hole.Width / 2, _hole.Y + _hole.Height / 2, 0, 0);
        Hole.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(from, _hole, Ms(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        });
        Animate(Dim, OpacityProperty, Dim.Opacity, step.Soft ? SoftDim : 1, 350);

        await Task.Delay(entrance ? 150 : 250);
        if (gen != _generation) return;
        Animate(Bubble, OpacityProperty, 0, 1, 200, new QuadraticEase { EasingMode = EasingMode.EaseOut });
        Animate(BubbleShift, TranslateTransform.YProperty, 8, 0, 200, new QuadraticEase { EasingMode = EasingMode.EaseOut });
        Animate(Guide, OpacityProperty, 0, 1, entrance ? 400 : 200);
        Animate(GuideShift, TranslateTransform.YProperty, entrance ? 16 : 8, 0, entrance ? 400 : 200,
                entrance ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
                         : new QuadraticEase { EasingMode = EasingMode.EaseOut });
        NextButton.Focus();

        if (_target is null) return;
        await Task.Delay(entrance ? 250 : 100);
        if (gen != _generation) return;
        PulseHalo(step.Soft ? 1 : 3);
    }

    private void FillBubble(TutorialStep step)
    {
        bool first = _index == 0, last = _index == _steps.Count - 1;
        StepTitle.Text = step.Title;
        StepBody.Text = step.Body;
        NextButton.Content = first ? TutorialScript.StartLabel : last ? TutorialScript.DoneLabel : TutorialScript.NextLabel;
        BackButton.Content = TutorialScript.BackLabel;
        SkipButton.Content = TutorialScript.SkipLabel;
        BackButton.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;

        Dots.Children.Clear();
        for (int i = 0; i < _steps.Count; i++)
            Dots.Children.Add(new Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(0, 0, 4, 0),
                Fill = (Brush)FindResource(i == _index ? "GoldBrush" : "TextMutedBrush"),
                Opacity = i == _index ? 1 : 0.45,
            });
    }

    /// <summary>Put the hole, halo, bubble and the guide where the current step wants them, with no
    /// animation. Also the whole of the resize response.</summary>
    private void LayoutStep(TutorialStep step)
    {
        var area = Root.RenderSize;
        bool small = area.Width < 560 || area.Height < 560;

        _hole = _target ?? new Rect(area.Width / 2, area.Height / 2, 0, 0);
        Hole.Rect = _hole;
        if (_target is Rect t)
        {
            Canvas.SetLeft(Halo, t.X - 4);
            Canvas.SetTop(Halo, t.Y - 4);
            Halo.Width = t.Width + 8;
            Halo.Height = t.Height + 8;
        }

        // Sized by HEIGHT: the poses share a height but not a width (the fishing one is wide).
        double guideHeight = _target is null ? (small ? 170 : 230) : small ? 140 : 190;
        var pose = Pose(TutorialScript.PoseFile(step.Pose, targetIsLeftOfGuide: true));
        var guide = new Size(guideHeight * pose.PixelWidth / pose.PixelHeight, guideHeight);
        Bubble.Width = Math.Min(small ? 230 : 280, Math.Max(160, area.Width - 2 * TutorialLayout.Edge));
        Bubble.Measure(new Size(Bubble.Width, double.PositiveInfinity));
        var bubble = new Size(Bubble.Width, Bubble.DesiredSize.Height);

        var place = TutorialLayout.Place(area, _target, bubble, guide);
        if (step.Pose == TutorialScript.PosePoint && _target is Rect aim)
        {
            // Point towards the spotlight. Both pointing poses are close enough in width that the
            // placement computed with one holds for the other.
            bool left = aim.X + aim.Width / 2 < place.Guide.X + guide.Width / 2;
            pose = Pose(TutorialScript.PoseFile(step.Pose, left));
        }
        Guide.Source = pose;
        Guide.Width = guide.Width;
        Guide.Height = guide.Height;
        Canvas.SetLeft(Bubble, place.Bubble.X);
        Canvas.SetTop(Bubble, place.Bubble.Y);
        Canvas.SetLeft(Guide, place.Guide.X);
        Canvas.SetTop(Guide, place.Guide.Y);
    }

    private static BitmapImage Pose(string file)
    {
        if (Poses.TryGetValue(file, out var image)) return image;
        image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/PWRUHelper;component/assets/snufkin/{file}.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return Poses[file] = image;
    }

    private Rect? MeasureTarget(FrameworkElement? control, double padding)
    {
        if (control is not { IsVisible: true } || control.ActualWidth <= 0 || control.ActualHeight <= 0) return null;
        Rect r;
        try { r = control.TransformToVisual(Root).TransformBounds(new Rect(control.RenderSize)); }
        catch (InvalidOperationException) { return null; }   // not in this window's visual tree
        r.Inflate(padding, padding);
        r.Intersect(new Rect(Root.RenderSize));
        return r.IsEmpty || r.Width <= 0 ? null : r;
    }

    // ---- input ------------------------------------------------------------------------------

    private void Next_Click(object sender, RoutedEventArgs e) => Next();
    private void Back_Click(object sender, RoutedEventArgs e) => Back();
    private void Skip_Click(object sender, RoutedEventArgs e) => Close(finished: false);

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsActive || Bubble.IsMouseOver) return;
        if (_target is not null && _hole.Contains(e.GetPosition(Root))) { Next(); return; }
        // A click on the dim does nothing but a tiny nudge, so the page never looks frozen.
        BubbleShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 4, Ms(75)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FullRect.Rect = new Rect(e.NewSize);
        if (!IsActive || _index >= _steps.Count) return;
        var step = _steps[_index];
        _target = MeasureTarget(step.Target is null ? null : _resolve(step.Target), step.Padding);
        Hole.BeginAnimation(RectangleGeometry.RectProperty, null);
        LayoutStep(step);
    }

    // ---- animation helpers ------------------------------------------------------------------

    private void PulseHalo(int cycles)
    {
        Halo.Opacity = 0.6;
        var pulse = new DoubleAnimation(0.6, 0.15, Ms(600))
        {
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(cycles), FillBehavior = FillBehavior.Stop,
        };
        Halo.BeginAnimation(OpacityProperty, pulse);
        var grow = new DoubleAnimation(1, 1.06, Ms(600))
        {
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(cycles), FillBehavior = FillBehavior.Stop,
        };
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void StopHalo()
    {
        Halo.BeginAnimation(OpacityProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Halo.Opacity = 0;
    }

    /// <summary>Clears every animation this layer can run and leaves it blank.</summary>
    private void StopAll()
    {
        BeginAnimation(OpacityProperty, null);
        Dim.BeginAnimation(OpacityProperty, null);
        Hole.BeginAnimation(RectangleGeometry.RectProperty, null);
        StopHalo();
        foreach (var e in new UIElement[] { Bubble, Guide }) { e.BeginAnimation(OpacityProperty, null); e.Opacity = 0; }
        BubbleShift.BeginAnimation(TranslateTransform.XProperty, null);
        BubbleShift.BeginAnimation(TranslateTransform.YProperty, null);
        GuideShift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>Set the END value as the base value, then animate towards it with FillBehavior.Stop:
    /// once it has played, no clock is left holding the property.</summary>
    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to,
                                int ms, IEasingFunction? ease = null)
    {
        ((DependencyObject)target).SetValue(property, to);
        target.BeginAnimation(property, new DoubleAnimation(from, to, Ms(ms))
        {
            EasingFunction = ease, FillBehavior = FillBehavior.Stop,
        });
    }

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));
}
