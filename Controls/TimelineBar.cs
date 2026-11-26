using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VideoCropper.Models;

namespace VideoCropper.Controls;

/// <summary>
/// Timeline zoomable : règle graduée, tête de lecture, plage début/fin, mini-vue d'ensemble.
///  - Clic gauche / glisser : positionner la tête de lecture (image par image)
///  - Glisser les repères verts : ajuster début / fin
///  - Molette : zoom centré sur le curseur ; Maj + molette : défilement
///  - Clic droit ou milieu + glisser : déplacer la vue
///  - Clic / glisser sur la mini-vue du bas : naviguer rapidement
/// </summary>
public class TimelineBar : FrameworkElement
{
    const double RulerH = 18, OverviewH = 6, Grab = 8;

    enum Drag { None, Scrub, Start, End, Pan, Overview }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static SolidColorBrush Br(byte r, byte g, byte b, byte a = 255) => Frozen(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    static readonly Brush BgBrush = Br(0x26, 0x26, 0x26);
    static readonly Brush TrackBrush = Br(0x3A, 0x3A, 0x3A);
    static readonly Brush SelBrush = Br(0x2E, 0x8B, 0x57, 150);
    static readonly Brush GreenBrush = Br(0x3C, 0xC8, 0x78);
    static readonly Brush RedBrush = Br(0xFF, 0x4D, 0x4D);
    static readonly Brush TextBrush = Br(0xBB, 0xBB, 0xBB);
    static readonly Brush WaveBrush = Br(0x9E, 0xD0, 0xFF, 210);
    static readonly Brush OverviewBg = Br(0x4A, 0x4A, 0x4A);
    static readonly Pen TickPen = Frozen(new Pen(Br(0x88, 0x88, 0x88), 1));
    static readonly Pen MinorPen = Frozen(new Pen(Br(0x66, 0x66, 0x66), 1));
    static readonly Pen PlayPen = Frozen(new Pen(RedBrush, 2));
    static readonly Pen StartPen = Frozen(new Pen(GreenBrush, 2));
    static readonly Pen ViewPen = Frozen(new Pen(Brushes.White, 1));
    static readonly double[] Steps =
        { 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };

    double _duration, _fps = 30;
    double _viewStart, _viewLen = 1;
    double _pos, _start, _end;
    Drag _drag;
    double _panOrigStart;
    Point _panOrigPoint;
    bool _panMoved;

    public event EventHandler? ValueChanged;
    public event EventHandler? RangeChanged;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCompleted;

    WaveformData? _wave;

    /// <summary>Enveloppe audio à dessiner dans la piste (null = rien).</summary>
    public WaveformData? Waveform
    {
        get => _wave;
        set { _wave = value; InvalidateVisual(); }
    }

    /// <summary>À appeler quand de nouvelles données de forme d'onde sont disponibles.</summary>
    public void RefreshWaveform() => InvalidateVisual();

    /// <summary>Vrai pendant un glissement de la tête de lecture (et non d'un repère début/fin).</summary>
    public bool IsScrubbingPlayhead => _drag == Drag.Scrub;

    public double Duration => _duration;
    public double Maximum => Math.Max(0.001, _duration - 1 / _fps);
    public double Start => _start;
    public double End => _end;

    public double Value
    {
        get => _pos;
        set
        {
            value = Math.Clamp(value, 0, Maximum);
            if (Math.Abs(value - _pos) < 1e-9) return;
            _pos = value;
            if (_drag == Drag.None) EnsureVisible(_pos);
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TimelineBar()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    // ------------------------------------------------------------ API
    public void Configure(double duration, double fps)
    {
        _duration = Math.Max(0, duration);
        _fps = fps > 0 ? fps : 30;
        _viewStart = 0;
        _viewLen = Math.Max(0.001, _duration);
        _pos = 0;
        _start = 0;
        _end = _duration;
        InvalidateVisual();
    }

    /// <summary>Met à jour la plage sans déclencher d'événement.</summary>
    public void SetRange(double start, double end)
    {
        _start = start;
        _end = end;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _viewStart = 0;
        _viewLen = Math.Max(0.001, _duration);
        InvalidateVisual();
    }

    // ------------------------------------------------------------ Vue
    double MinLen => Math.Min(_duration, 1.0);
    double X(double t) => (t - _viewStart) / _viewLen * ActualWidth;
    double T(double x) => _viewStart + x / Math.Max(1, ActualWidth) * _viewLen;
    double Snap(double t) => Math.Round(t * _fps) / _fps;

    void ClampView()
    {
        if (_duration <= 0) return;
        _viewLen = Math.Clamp(_viewLen, MinLen, _duration);
        _viewStart = Math.Clamp(_viewStart, 0, _duration - _viewLen);
    }

    void EnsureVisible(double t)
    {
        if (_duration <= 0) return;
        if (t < _viewStart) _viewStart = t - 0.1 * _viewLen;
        else if (t > _viewStart + _viewLen) _viewStart = t - 0.9 * _viewLen;
        else return;
        ClampView();
    }

    double TrackTop => RulerH;
    double TrackBottom => ActualHeight - OverviewH - 3;

    // ------------------------------------------------------------ Rendu
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (_duration <= 0 || w <= 0) return;

        double top = TrackTop, bottom = TrackBottom;
        dc.DrawRectangle(TrackBrush, null, new Rect(0, top, w, Math.Max(0, bottom - top)));

        // Plage sélectionnée
        double xs = X(_start), xe = X(_end);
        double l = Math.Max(0, xs), r = Math.Min(w, xe);
        if (r > l) dc.DrawRectangle(SelBrush, null, new Rect(l, top, r - l, Math.Max(0, bottom - top)));

        DrawWaveform(dc, w, top, bottom);
        DrawRuler(dc, w, top);

        // Repères début / fin
        DrawMarker(dc, xs, top, bottom, true, w);
        DrawMarker(dc, xe, top, bottom, false, w);

        // Tête de lecture
        double xp = X(_pos);
        if (xp >= -6 && xp <= w + 6)
        {
            dc.DrawLine(PlayPen, new Point(xp, 0), new Point(xp, bottom));
            dc.DrawGeometry(RedBrush, null, Poly(new Point(xp - 6, 0), new Point(xp + 6, 0), new Point(xp, 9)));
        }

        // Indicateur de zoom
        if (_viewLen < _duration * 0.995)
        {
            var t = Text($"×{_duration / _viewLen:0.#}", 10, TextBrush);
            dc.DrawText(t, new Point(w - t.Width - 4, 1));
        }

        DrawOverview(dc, w, h);
    }

    /// <summary>Niveau d'affichage 0..1 d'un pic (échelle en dB, de -54 dB à 0 dB).</summary>
    static double Level(int peak)
    {
        if (peak <= 0) return 0;
        double db = 20 * Math.Log10(peak / 32768.0);
        return Math.Clamp((db + 54) / 54, 0, 1);
    }

    /// <summary>
    /// Forme d'onde (symétrique) : pour chaque colonne de pixels, le pic maximal des tranches de 5 ms
    /// couvertes par cette colonne. S'adapte donc automatiquement au zoom et au défilement.
    /// </summary>
    void DrawWaveform(DrawingContext dc, double w, double top, double bottom)
    {
        var wf = _wave;
        if (wf == null || _duration <= 0) return;
        int filled = wf.Filled;
        if (filled <= 0) return;

        double mid = (top + bottom) / 2, half = (bottom - top) / 2 - 1;
        if (half <= 1) return;

        var peaks = wf.Peaks;
        double bps = WaveformData.BinsPerSecond;
        int cols = (int)Math.Ceiling(w);
        var up = new List<Point>(cols);
        var down = new List<Point>(cols);

        for (int x = 0; x < cols; x++)
        {
            int b0 = Math.Max(0, (int)Math.Floor(T(x) * bps));
            if (b0 >= filled) break; // la suite n'est pas encore calculée
            int b1 = Math.Min(filled, Math.Max(b0 + 1, (int)Math.Ceiling(T(x + 1) * bps)));

            int max = 0;
            for (int b = b0; b < b1; b++)
                if (peaks[b] > max) max = peaks[b];

            double h = Math.Max(0.5, Level(max) * half);
            up.Add(new Point(x + 0.5, mid - h));
            down.Add(new Point(x + 0.5, mid + h));
        }
        if (up.Count < 2) return;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(up[0], true, true);
            ctx.PolyLineTo(up, true, false);
            down.Reverse();
            ctx.PolyLineTo(down, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(WaveBrush, null, g);
    }

    void DrawRuler(DrawingContext dc, double w, double top)
    {
        double pps = w / _viewLen;
        double step = Steps[^1];
        foreach (var s in Steps) if (s * pps >= 90) { step = s; break; }

        double minor = step / 5;
        if (minor * pps >= 8)
        {
            for (double t = Math.Ceiling(_viewStart / minor) * minor; t <= _viewStart + _viewLen; t += minor)
            {
                double x = X(t);
                dc.DrawLine(MinorPen, new Point(x, top - 4), new Point(x, top));
            }
        }
        for (double t = Math.Ceiling(_viewStart / step) * step; t <= _viewStart + _viewLen + 1e-9; t += step)
        {
            double x = X(t);
            dc.DrawLine(TickPen, new Point(x, top - 9), new Point(x, top));
            var ts = TimeSpan.FromSeconds(t);
            string label = step < 1 ? ts.ToString(@"h\:mm\:ss\.ff") : ts.ToString(@"h\:mm\:ss");
            dc.DrawText(Text(label, 10, TextBrush), new Point(x + 3, 1));
        }
    }

    void DrawMarker(DrawingContext dc, double x, double top, double bottom, bool isStart, double w)
    {
        if (x < -10 || x > w + 10) return;
        dc.DrawLine(StartPen, new Point(x, top), new Point(x, bottom));
        double d = isStart ? 10 : -10;
        dc.DrawGeometry(GreenBrush, null, Poly(new Point(x, top), new Point(x + d, top), new Point(x, top + 10)));
    }

    void DrawOverview(DrawingContext dc, double w, double h)
    {
        double y = h - OverviewH;
        dc.DrawRectangle(OverviewBg, null, new Rect(0, y, w, OverviewH));
        double sx = _start / _duration * w, ex = _end / _duration * w;
        dc.DrawRectangle(GreenBrush, null, new Rect(sx, y, Math.Max(1, ex - sx), OverviewH));
        double vx = _viewStart / _duration * w, vw = Math.Max(3, _viewLen / _duration * w);
        dc.DrawRectangle(null, ViewPen, new Rect(vx, y, Math.Min(vw, w - vx), OverviewH));
        double px = _pos / _duration * w;
        dc.DrawLine(PlayPen, new Point(px, y), new Point(px, h));
    }

    static StreamGeometry Poly(Point a, Point b, Point c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(b, true, false);
            ctx.LineTo(c, true, false);
        }
        g.Freeze();
        return g;
    }

    FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ------------------------------------------------------------ Souris
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0 || _drag != Drag.None) return;
        var p = e.GetPosition(this);

        if (p.Y >= ActualHeight - OverviewH - 3)
        {
            _drag = Drag.Overview;
            CenterViewAt(p.X);
        }
        else
        {
            double dStart = Math.Abs(p.X - X(_start)), dEnd = Math.Abs(p.X - X(_end));
            if (dStart <= Grab && dStart <= dEnd) _drag = Drag.Start;
            else if (dEnd <= Grab) _drag = Drag.End;
            else _drag = Drag.Scrub;
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            ApplyDrag(p);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is Drag.Scrub or Drag.Start or Drag.End or Drag.Overview)
        {
            bool scrub = _drag != Drag.Overview;
            _drag = Drag.None;
            ReleaseMouseCapture();
            if (scrub) ScrubCompleted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle && _drag == Drag.None && _duration > 0)
        {
            _drag = Drag.Pan;
            _panOrigStart = _viewStart;
            _panOrigPoint = e.GetPosition(this);
            _panMoved = false;
            Cursor = Cursors.Hand;
            CaptureMouse();
            e.Handled = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.Pan && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _drag = Drag.None;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            // Clic droit simple (sans déplacement) : on laisse passer l'événement → menu contextuel
            if (_panMoved || e.ChangedButton == MouseButton.Middle) e.Handled = true;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_duration <= 0) return;
        var p = e.GetPosition(this);

        switch (_drag)
        {
            case Drag.Scrub:
            case Drag.Start:
            case Drag.End:
                ApplyDrag(p);
                break;
            case Drag.Overview:
                CenterViewAt(p.X);
                break;
            case Drag.Pan:
                if (!_panMoved && Math.Abs(p.X - _panOrigPoint.X) > 3) _panMoved = true;
                if (_panMoved)
                {
                    _viewStart = _panOrigStart - (p.X - _panOrigPoint.X) / ActualWidth * _viewLen;
                    ClampView();
                    InvalidateVisual();
                }
                break;
            default:
                bool nearMarker = p.Y < ActualHeight - OverviewH - 3 &&
                                  (Math.Abs(p.X - X(_start)) <= Grab || Math.Abs(p.X - X(_end)) <= Grab);
                Cursor = nearMarker ? Cursors.SizeWE : Cursors.Arrow;
                break;
        }
    }

    void ApplyDrag(Point p)
    {
        double t = Snap(T(Math.Clamp(p.X, 0, ActualWidth)));
        double frame = 1 / _fps;
        switch (_drag)
        {
            case Drag.Scrub:
                Value = t;
                break;
            case Drag.Start:
                double s = Math.Clamp(t, 0, _end - frame);
                if (Math.Abs(s - _start) > 1e-9)
                {
                    _start = s;
                    RangeChanged?.Invoke(this, EventArgs.Empty);
                }
                Value = s;
                InvalidateVisual();
                break;
            case Drag.End:
                double en = Math.Clamp(t, _start + frame, _duration);
                if (Math.Abs(en - _end) > 1e-9)
                {
                    _end = en;
                    RangeChanged?.Invoke(this, EventArgs.Empty);
                }
                Value = en - frame; // affiche la dernière image conservée
                InvalidateVisual();
                break;
        }
    }

    void CenterViewAt(double x)
    {
        double t = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * _duration;
        _viewStart = t - _viewLen / 2;
        ClampView();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_duration <= 0) return;
        var p = e.GetPosition(this);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _viewStart -= Math.Sign(e.Delta) * _viewLen * 0.15;
        }
        else
        {
            double tc = T(p.X);
            _viewLen = Math.Clamp(_viewLen / Math.Pow(1.0025, e.Delta), MinLen, _duration);
            _viewStart = tc - p.X / Math.Max(1, ActualWidth) * _viewLen; // le temps sous le curseur ne bouge pas
        }
        ClampView();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
