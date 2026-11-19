using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoCropper.Controls;

/// <summary>
/// Affiche une image et permet de dessiner / déplacer / redimensionner un rectangle (ROI).
/// La ROI est stockée en coordonnées normalisées (0..1) : indépendante de la résolution d'aperçu.
/// </summary>
public class RoiSelector : FrameworkElement
{
    const double HandleSize = 10, HitTol = 10;

    enum Mode { None, Draw, Move, Resize }

    ImageSource? _img;
    double _aspect = 16.0 / 9;
    Rect _roi = Rect.Empty;
    Mode _mode;
    int _handle;
    Point _anchor, _grab;
    Rect _origRoi;
    double _l, _t, _r, _b;

    // Vue : zoom et point de l'image (normalisé) affiché au centre du contrôle
    double _zoom = 1, _cx = 0.5, _cy = 0.5;
    bool _panning;
    Point _panStart;
    double _panCx, _panCy;

    public event EventHandler? RoiChanged;
    public event EventHandler? ZoomChanged;
    public double Zoom => _zoom;

    public void ResetZoom()
    {
        _zoom = 1; _cx = _cy = 0.5;
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public double Aspect { get => _aspect; set { _aspect = value; InvalidateVisual(); } }
    public Rect NormalizedRoi => _roi;

    public RoiSelector()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public void SetImage(ImageSource img) { _img = img; InvalidateVisual(); }

    /// <summary>Définit la ROI (coordonnées normalisées 0..1).</summary>
    public void SetRoi(Rect normalized)
    {
        if (normalized.IsEmpty) _roi = Rect.Empty;
        else
        {
            double x = Math.Clamp(normalized.X, 0, 1), y = Math.Clamp(normalized.Y, 0, 1);
            double r = Math.Clamp(normalized.Right, 0, 1), b = Math.Clamp(normalized.Bottom, 0, 1);
            _roi = new Rect(new Point(x, y), new Point(r, b));
        }
        InvalidateVisual();
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRoi()
    {
        _roi = Rect.Empty;
        InvalidateVisual();
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ Géométrie
    Size FitSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return Size.Empty;
        double w = ActualWidth, h = w / _aspect;
        if (h > ActualHeight) { h = ActualHeight; w = h * _aspect; }
        return new Size(w, h);
    }

    Rect ImageRect()
    {
        var f = FitSize();
        if (f.IsEmpty) return Rect.Empty;
        double w = f.Width * _zoom, h = f.Height * _zoom;
        return new Rect(ActualWidth / 2 - _cx * w, ActualHeight / 2 - _cy * h, w, h);
    }

    void ClampView()
    {
        var f = FitSize();
        if (f.IsEmpty) return;
        double w = f.Width * _zoom, h = f.Height * _zoom;
        _cx = w <= ActualWidth ? 0.5 : Math.Clamp(_cx, ActualWidth / 2 / w, 1 - ActualWidth / 2 / w);
        _cy = h <= ActualHeight ? 0.5 : Math.Clamp(_cy, ActualHeight / 2 / h, 1 - ActualHeight / 2 / h);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClampView();
    }

    // ------------------------------------------------------------ Zoom / déplacement de la vue
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var ir = ImageRect();
        if (ir.IsEmpty) return;
        var p = e.GetPosition(this);
        double nx = (p.X - ir.X) / ir.Width, ny = (p.Y - ir.Y) / ir.Height;

        double z = Math.Clamp(_zoom * Math.Pow(1.0015, e.Delta), 1, 24);
        if (Math.Abs(z - _zoom) < 1e-9) return;
        _zoom = z;

        // Le point sous le curseur reste sous le curseur
        var f = FitSize();
        double w = f.Width * z, h = f.Height * z;
        double left = p.X - nx * w, top = p.Y - ny * h;
        _cx = (ActualWidth / 2 - left) / w;
        _cy = (ActualHeight / 2 - top) / h;
        ClampView();

        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle && _mode == Mode.None)
        {
            _panning = true;
            _panStart = e.GetPosition(this);
            _panCx = _cx; _panCy = _cy;
            Cursor = Cursors.Hand;
            CaptureMouse();
            e.Handled = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            e.Handled = true;
        }
        base.OnMouseUp(e);
    }

    static Rect ToScreen(Rect n, Rect ir) =>
        new(ir.X + n.X * ir.Width, ir.Y + n.Y * ir.Height, n.Width * ir.Width, n.Height * ir.Height);

    static Point ToNorm(Point p, Rect ir) =>
        new(Math.Clamp((p.X - ir.X) / ir.Width, 0, 1), Math.Clamp((p.Y - ir.Y) / ir.Height, 0, 1));

    static Point[] HandlePoints(Rect s) => new Point[]
    {
        s.TopLeft, new(s.X + s.Width / 2, s.Y), s.TopRight,
        new(s.Right, s.Y + s.Height / 2), s.BottomRight,
        new(s.X + s.Width / 2, s.Bottom), s.BottomLeft, new(s.X, s.Y + s.Height / 2)
    };

    int HitHandle(Point p, Rect ir)
    {
        if (_roi.IsEmpty) return -1;
        var pts = HandlePoints(ToScreen(_roi, ir));
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(pts[i].X - p.X) <= HitTol && Math.Abs(pts[i].Y - p.Y) <= HitTol) return i;
        return -1;
    }

    static Rect R(double x, double y, double w, double h) => new(x, y, Math.Max(0, w), Math.Max(0, h));

    // ------------------------------------------------------------ Rendu
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var ir = ImageRect();
        if (ir.IsEmpty) return;
        if (_img != null) dc.DrawImage(_img, ir);
        if (_roi.IsEmpty) return;

        var s = ToScreen(_roi, ir);
        var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        dc.DrawRectangle(dim, null, R(ir.Left, ir.Top, ir.Width, s.Top - ir.Top));
        dc.DrawRectangle(dim, null, R(ir.Left, s.Bottom, ir.Width, ir.Bottom - s.Bottom));
        dc.DrawRectangle(dim, null, R(ir.Left, s.Top, s.Left - ir.Left, s.Height));
        dc.DrawRectangle(dim, null, R(s.Right, s.Top, ir.Right - s.Right, s.Height));

        dc.DrawRectangle(null, new Pen(Brushes.Black, 3), s);
        dc.DrawRectangle(null, new Pen(Brushes.Yellow, 1.5), s);

        // Tiers de règle
        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 0)), 1);
        for (int k = 1; k <= 2; k++)
        {
            dc.DrawLine(thirds, new Point(s.X + s.Width * k / 3, s.Y), new Point(s.X + s.Width * k / 3, s.Bottom));
            dc.DrawLine(thirds, new Point(s.X, s.Y + s.Height * k / 3), new Point(s.Right, s.Y + s.Height * k / 3));
        }

        foreach (var pt in HandlePoints(s))
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1),
                new Rect(pt.X - HandleSize / 2, pt.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    // ------------------------------------------------------------ Souris
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var ir = ImageRect();
        if (ir.IsEmpty) return;
        var p = e.GetPosition(this);
        int h = HitHandle(p, ir);

        if (h >= 0)
        {
            _mode = Mode.Resize; _handle = h;
            _l = _roi.Left; _t = _roi.Top; _r = _roi.Right; _b = _roi.Bottom;
        }
        else if (!_roi.IsEmpty && ToScreen(_roi, ir).Contains(p))
        {
            _mode = Mode.Move; _grab = ToNorm(p, ir); _origRoi = _roi;
        }
        else
        {
            _mode = Mode.Draw; _anchor = ToNorm(p, ir); _roi = new Rect(_anchor, _anchor);
        }
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning)
        {
            var f = FitSize();
            var pos = e.GetPosition(this);
            _cx = _panCx - (pos.X - _panStart.X) / (f.Width * _zoom);
            _cy = _panCy - (pos.Y - _panStart.Y) / (f.Height * _zoom);
            ClampView();
            InvalidateVisual();
            return;
        }
        var ir = ImageRect();
        if (ir.IsEmpty) return;
        var raw = e.GetPosition(this);

        if (_mode == Mode.None)
        {
            int h = HitHandle(raw, ir);
            Cursor = h switch
            {
                0 or 4 => Cursors.SizeNWSE,
                2 or 6 => Cursors.SizeNESW,
                1 or 5 => Cursors.SizeNS,
                3 or 7 => Cursors.SizeWE,
                _ => !_roi.IsEmpty && ToScreen(_roi, ir).Contains(raw) ? Cursors.SizeAll : Cursors.Cross
            };
            return;
        }

        var p = ToNorm(raw, ir);
        switch (_mode)
        {
            case Mode.Draw:
                _roi = new Rect(_anchor, p);
                break;
            case Mode.Move:
                double dx = p.X - _grab.X, dy = p.Y - _grab.Y;
                _roi = new Rect(
                    Math.Clamp(_origRoi.X + dx, 0, 1 - _origRoi.Width),
                    Math.Clamp(_origRoi.Y + dy, 0, 1 - _origRoi.Height),
                    _origRoi.Width, _origRoi.Height);
                break;
            case Mode.Resize:
                if (_handle is 0 or 6 or 7) _l = p.X;
                if (_handle is 2 or 3 or 4) _r = p.X;
                if (_handle is 0 or 1 or 2) _t = p.Y;
                if (_handle is 4 or 5 or 6) _b = p.Y;
                _roi = new Rect(new Point(Math.Min(_l, _r), Math.Min(_t, _b)),
                                new Point(Math.Max(_l, _r), Math.Max(_t, _b)));
                break;
        }
        InvalidateVisual();
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_mode == Mode.None) return;
        ReleaseMouseCapture();
        _mode = Mode.None;
        // Un simple clic (ou un rectangle minuscule) efface la ROI
        if (!_roi.IsEmpty && (_roi.Width < 0.01 || _roi.Height < 0.01)) _roi = Rect.Empty;
        InvalidateVisual();
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }
}
