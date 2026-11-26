using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;
using VideoCropper.Models;
using VideoCropper.Services;

namespace VideoCropper;

public partial class MainWindow : Window
{
    const string AppTitle = "Video Cropper";

    VideoInfo? _info;
    double _start, _end;
    List<string> _available = new();

    // Géométrie de sortie. Tout (aperçu, ROI, champs) est exprimé dans l'orientation AFFICHÉE :
    // rotation des métadonnées (appliquée par FFmpeg) + rotation choisie par l'utilisateur.
    int _userRot;                 // 0, 90, 180, 270 (sens horaire)
    ScaleMode _scaleMode;
    double _scaleValue;
    bool _suppress;               // évite les ré-entrées lors des changements programmatiques
    int EffW => _info == null ? 0 : _info.Oriented(_userRot).W;
    int EffH => _info == null ? 0 : _info.Oriented(_userRot).H;

    // Export
    CancellationTokenSource? _exportCts;
    bool _exporting;

    // Aperçu (extraction d'une image)
    bool _dragging, _extracting, _pendingFast, _resumeAfterScrub;
    double? _pendingT;

    // Lecture
    bool _playing, _playStopAtEnd;
    CancellationTokenSource? _playCts;
    WriteableBitmap? _playBmp;

    public MainWindow()
    {
        InitializeComponent();
        Roi.RoiChanged += OnRoiChanged;
        Roi.ZoomChanged += (_, _) => ZoomText.Text = Roi.Zoom > 1.01 ? $"Zoom ×{Roi.Zoom:0.#}" : "";

        // Clic / glissement sur la timeline pendant la lecture : la lecture reprend à la nouvelle position au relâchement
        Timeline.ScrubStarted += (_, _) =>
        {
            _resumeAfterScrub = _playing && _jogDir == 0 && Timeline.IsScrubbingPlayhead;
            StopPlayback();
            _dragging = true;
        };
        Timeline.ScrubCompleted += (_, _) =>
        {
            _dragging = false;
            if (_resumeAfterScrub)
            {
                _resumeAfterScrub = false;
                _ = StartPlaybackAsync();
            }
            else RequestFrame(); // image exacte au relâchement
        };
        Timeline.ValueChanged += (_, _) =>
        {
            if (_info == null) return;
            UpdateTimeline();
            RequestFrame(); // sans effet pendant la lecture
        };
        Timeline.RangeChanged += (_, _) =>
        {
            _start = Timeline.Start;
            _end = Timeline.End;
            UpdateTimeline();
        };

        Closed += (_, _) => { _playCts?.Cancel(); _audio?.Dispose(); _waveCts?.Cancel(); };

        Loaded += async (_, _) =>
        {
            Focus();
            await InitEncodersAsync();
        };
    }

    // ================================================================ Encodeurs
    static readonly Dictionary<string, string> Labels = new()
    {
        ["libx265"] = "HEVC / H.265 — logiciel (x265)",
        ["libx264"] = "H.264 — logiciel (x264)",
        ["hevc_nvenc"] = "HEVC — NVIDIA NVENC",
        ["h264_nvenc"] = "H.264 — NVIDIA NVENC",
        ["hevc_qsv"] = "HEVC — Intel QuickSync",
        ["h264_qsv"] = "H.264 — Intel QuickSync",
        ["hevc_amf"] = "HEVC — AMD AMF",
        ["h264_amf"] = "H.264 — AMD AMF",
    };

    async Task InitEncodersAsync()
    {
        try
        {
            Status.Text = "Détection des encodeurs…";
            _available = await FfmpegService.DetectEncodersAsync();
        }
        catch (Exception ex)
        {
            Status.Text = "FFmpeg introuvable";
            MessageBox.Show("Impossible de lancer FFmpeg.\n\nPlacez ffmpeg.exe et ffprobe.exe à côté de l'application " +
                            "ou ajoutez-les au PATH (winget install Gyan.FFmpeg).\n\n" + ex.Message,
                "FFmpeg", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var items = new List<EncoderItem>
        {
            new("Copie sans ré-encodage (uniquement sans ROI)", "copy"),
            new("Auto — même codec que la source (logiciel)", "auto")
        };
        items.AddRange(_available.Select(e => new EncoderItem(Labels.GetValueOrDefault(e, e), e)));
        EncoderBox.ItemsSource = items;
        EncoderBox.SelectedIndex = 0;
        Status.Text = "Prêt — encodeurs : " + string.Join(", ", _available);
    }

    // ================================================================ Chargement
    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Vidéos|*.mp4;*.mov;*.mkv;*.lrv;*.avi|Tous les fichiers|*.*"
        };
        if (dlg.ShowDialog() == true) _ = LoadAsync(dlg.FileName);
    }

    void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _ = LoadAsync(files[0]);
    }

    async Task LoadAsync(string path)
    {
        if (_exporting) return;
        StopPlayback();

        VideoInfo info;
        try { info = await FfmpegService.ProbeAsync(path); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lecture impossible", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _info = info;
        _suppress = true;
        _userRot = 0; RotationBox.SelectedIndex = 0;
        _scaleMode = ScaleMode.None; ResizeBox.SelectedIndex = 0;
        _suppress = false;
        DropZone.Visibility = Visibility.Collapsed; // le cadre d'accueil disparaît une fois une vidéo chargée
        Title = AppTitle;
        Roi.ResetZoom();
        Roi.Aspect = (double)EffW / EffH;
        Roi.ClearRoi();

        Timeline.Configure(info.Duration, info.Fps);
        _waveCts?.Cancel();
        Timeline.Waveform = null;
        if (info.HasAudio) _ = LoadWaveformAsync(info);
        _start = 0;
        _end = info.Duration;

        string rotInfo = info.Rotation != 0 ? $" (rotation {info.Rotation}°)" : "";
        InfoText.Text = $"{System.IO.Path.GetFileName(path)} — {info.DisplayWidth}×{info.DisplayHeight}{rotInfo} @ {info.Fps:0.##} fps — " +
                        $"{info.VCodec} {info.PixFmt} — {info.BitrateKbps / 1000.0:0.#} Mbps — {Fmt(info.Duration)}";

        UpdateTimeline();
        UpdateRoiFields(null);
        RefreshGeometry();
        RequestFrame();
    }

    // ================================================================ Aperçu (image fixe)
    static string Fmt(double t) => TimeSpan.FromSeconds(Math.Max(0, t)).ToString(@"hh\:mm\:ss\.fff");

    /// <summary>
    /// Demande l'image à la position courante. Si une extraction est déjà en cours, seule la dernière
    /// position demandée sera traitée ensuite. Pendant le glissement : extraction rapide basse résolution.
    /// Sans effet pendant la lecture.
    /// </summary>
    void RequestFrame()
    {
        if (_info == null || _playing) return;
        _pendingT = Timeline.Value;
        _pendingFast = _dragging;
        if (!_extracting) _ = PumpFramesAsync();
    }

    async Task PumpFramesAsync()
    {
        _extracting = true;
        try
        {
            while (_pendingT is double t && _info != null)
            {
                bool fast = _pendingFast;
                _pendingT = null;
                try
                {
                    var bmp = await FfmpegService.ExtractFrameAsync(
                        _info.Path, t, fast ? 960 : Math.Min(EffW, 3840), _userRot, fast, CancellationToken.None);
                    if (!_playing) Roi.SetImage(bmp);
                }
                catch (Exception ex) { Status.Text = "Aperçu : " + ex.Message.Split('\n')[0]; }
            }
        }
        finally { _extracting = false; }
    }

    // ================================================================ Forme d'onde
    CancellationTokenSource? _waveCts;

    async Task LoadWaveformAsync(VideoInfo info)
    {
        _waveCts?.Cancel();
        var cts = _waveCts = new CancellationTokenSource();
        var data = new WaveformData(info.Duration);
        Timeline.Waveform = data;
        try
        {
            // Calcul hors du thread UI ; la timeline est redessinée régulièrement pendant le chargement
            await Task.Run(() => FfmpegService.LoadWaveformAsync(info, data,
                () => Dispatcher.BeginInvoke(new Action(Timeline.RefreshWaveform)), cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* pas de forme d'onde : sans importance */ }
    }

    // ================================================================ Volume
    AudioPlayer? _audio;
    double _volume = 0.8;
    bool _muted;

    void ApplyVolume()
    {
        try { if (_audio != null) _audio.Volume = _muted ? 0 : _volume; } catch { }
    }

    void UpdateMuteButton() => MuteBtn.Content = _muted ? "🔇" : "🔊";

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _volume = e.NewValue;
        _muted = _volume <= 0.001;
        UpdateMuteButton();
        ApplyVolume();
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        UpdateMuteButton();
        ApplyVolume();
    }

    // ================================================================ Lecture
    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (_info == null) return;
        if (_playing) StopPlayback();
        else _ = StartPlaybackAsync();
    }

    void UpdatePlayButton() => PlayBtn.Content = _playing ? "⏸ Pause" : "▶ Lecture";

    async Task StartPlaybackAsync(bool jog = false)
    {
        if (_info == null || _playing) return;
        var info = _info;

        double t0 = Timeline.Value;
        if (!jog && t0 >= Timeline.Maximum - 0.1) t0 = Math.Min(_start, Timeline.Maximum); // fin atteinte : on repart du début
        _playing = true;
        // Si on démarre avant le repère de fin, la lecture s'arrête dessus (aperçu de la découpe)
        _playStopAtEnd = !jog && t0 < _end - 1.0 / info.Fps;
        UpdatePlayButton();
        Timeline.Value = t0;

        var cts = _playCts = new CancellationTokenSource();

        // Son : décodé en parallèle, démarré avec la première image
        AudioPlayer? audio = null;
        if (info.HasAudio)
        {
            try { audio = new AudioPlayer(FfmpegService.FfmpegPath, info.Path, t0); }
            catch (Exception ex) { Status.Text = "Audio : " + ex.Message; }
        }
        _audio = audio;
        ApplyVolume();

        try
        {
            await FfmpegService.PlayAsync(info, t0, 960, _userRot, audio, ShowPlayFrame, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = "Lecture : " + ex.Message.Split('\n')[0]; }
        finally
        {
            audio?.Dispose();
            if (_audio == audio) _audio = null;
            if (_playCts == cts) // fin naturelle de la vidéo
            {
                _playing = false;
                UpdatePlayButton();
                RequestFrame();
            }
        }
    }

    void ShowPlayFrame(byte[] buf, int w, int h, double t)
    {
        if (_playBmp == null || _playBmp.PixelWidth != w || _playBmp.PixelHeight != h)
            _playBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        _playBmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 3, 0);
        Roi.SetImage(_playBmp);
        Timeline.Value = t;
        if (_playStopAtEnd && _info != null && t >= _end - 0.5 / _info.Fps) StopPlayback();
    }

    void StopPlayback()
    {
        if (!_playing) return;
        _playCts?.Cancel();
        _audio?.Dispose(); // silence immédiat
        _audio = null;
        _playing = false;
        UpdatePlayButton();
        RequestFrame(); // image exacte à la position d'arrêt
    }

    // ================================================================ Timeline
    void UpdateTimeline()
    {
        if (_info == null) return;
        Timeline.SetRange(_start, _end);
        TimeText.Text = $"{Fmt(Timeline.Value)} / {Fmt(_info.Duration)}";
        RangeText.Text = $"Durée {Fmt(_end - _start)}";
        if (!StartBox.IsKeyboardFocused) StartBox.Text = Fmt(_start);
        if (!EndBox.IsKeyboardFocused) EndBox.Text = Fmt(_end);
    }

    void Step(int dir, bool big)
    {
        if (_info == null) return;
        StopPlayback();
        double step = big ? 1.0 : 1.0 / _info.Fps;
        Timeline.Value = Math.Clamp(Timeline.Value + dir * step, 0, Timeline.Maximum);
    }

    // ---- Zone « image précédente / suivante »
    //  Clic gauche : image suivante ; maintenu : lecture (avec le son) jusqu'au relâchement.
    //  Clic droit  : image précédente ; maintenu : rembobinage à vitesse réelle jusqu'au relâchement.
    const int JogHoldMs = 350;
    int _jogDir;                       // 0 = inactif, 1 = avant (gauche), -1 = arrière (droit)
    bool _jogHolding;
    CancellationTokenSource? _jogHoldCts;

    async void Jog_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_info == null || _jogDir != 0) return;
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;

        _jogDir = e.ChangedButton == MouseButton.Left ? 1 : -1;
        JogZone.CaptureMouse();
        JogZone.LostMouseCapture -= Jog_LostCapture;
        JogZone.LostMouseCapture += Jog_LostCapture;
        e.Handled = true;

        Step(_jogDir, false); // réaction immédiate : une image

        var cts = _jogHoldCts = new CancellationTokenSource();
        try { await Task.Delay(JogHoldMs, cts.Token); }
        catch (OperationCanceledException) { return; } // relâché avant : simple clic

        if (_jogDir == 0 || _info == null) return;
        _jogHolding = true;
        if (_jogDir > 0)
        {
            Status.Text = "Lecture…";
            _ = StartPlaybackAsync(jog: true);
        }
        else
        {
            Status.Text = "Rembobinage…";
            _ = StartRewindAsync();
        }
    }

    void Jog_MouseUp(object sender, MouseButtonEventArgs e)
    {
        int dir = e.ChangedButton == MouseButton.Left ? 1 : e.ChangedButton == MouseButton.Right ? -1 : 0;
        if (dir == 0 || dir != _jogDir) return;
        EndJog();
        e.Handled = true;
    }

    void Jog_LostCapture(object sender, MouseEventArgs e)
    {
        if (_jogDir != 0) EndJog(); // fenêtre perdue (Alt+Tab…) : on arrête
    }

    void EndJog()
    {
        _jogHoldCts?.Cancel();
        _jogHoldCts = null;
        bool wasHolding = _jogHolding;
        _jogDir = 0;
        _jogHolding = false;
        JogZone.ReleaseMouseCapture();
        if (wasHolding)
        {
            StopPlayback();
            Status.Text = "";
        }
    }

    async Task StartRewindAsync()
    {
        if (_info == null || _playing) return;
        var info = _info;
        double t0 = Timeline.Value;
        if (t0 <= 0.001) { Status.Text = "Début de la vidéo"; return; }

        _playing = true;
        _playStopAtEnd = false;
        UpdatePlayButton();

        string? error = null;
        var cts = _playCts = new CancellationTokenSource();
        try
        {
            await FfmpegService.RewindAsync(info, t0, 800, _userRot, ShowRewindFrame, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (_playCts == cts) // relâché, début atteint ou erreur
            {
                _playing = false;
                UpdatePlayButton();
                RequestFrame(); // image exacte à la position d'arrêt
            }
        }

        if (error != null)
            MessageBox.Show("Rembobinage impossible :\n\n" + error, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void ShowRewindFrame(BitmapSource bmp, double t)
    {
        Roi.SetImage(bmp);
        Timeline.Value = t;
    }

    // Utilisent la position actuelle de la tête de lecture (y compris depuis le menu clic droit)
    void SetStart_Click(object? s = null, RoutedEventArgs? e = null)
    {
        if (_info == null) return;
        _start = Timeline.Value;
        if (_end <= _start) _end = _info.Duration;
        UpdateTimeline();
    }

    void SetEnd_Click(object? s = null, RoutedEventArgs? e = null)
    {
        if (_info == null) return;
        // La fin inclut l'image affichée
        _end = Math.Min(_info.Duration, Timeline.Value + 1 / _info.Fps);
        if (_start >= _end) _start = 0;
        UpdateTimeline();
    }

    void ResetRange_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        _start = 0;
        _end = _info.Duration;
        UpdateTimeline();
    }

    // ---- Saisie manuelle début / fin
    static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return false;
        var parts = text.Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    void StartBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        if (TryParseTime(StartBox.Text, out var t))
        {
            t = Math.Round(t * _info.Fps) / _info.Fps;
            _start = Math.Clamp(t, 0, _end - 1 / _info.Fps);
            Timeline.SetRange(_start, _end);
            Timeline.Value = Math.Min(_start, Timeline.Maximum); // affiche l'image du début
        }
        UpdateTimeline(); // reformate ou restaure la valeur
    }

    void EndBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        if (TryParseTime(EndBox.Text, out var t))
        {
            t = Math.Round(t * _info.Fps) / _info.Fps;
            _end = Math.Clamp(t, _start + 1 / _info.Fps, _info.Duration);
            Timeline.SetRange(_start, _end);
            Timeline.Value = Math.Max(0, _end - 1 / _info.Fps); // affiche la dernière image conservée
        }
        UpdateTimeline();
    }

    // Entrée = valider (le focus repart sur la fenêtre → LostFocus applique la valeur)
    void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            Focus();
            e.Handled = true;
        }
    }

    void Combo_DropDownClosed(object? sender, EventArgs e) => Focus(); // rend l'Espace à la lecture

    // ================================================================ Clavier / focus
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Open_Click(this, e);
            e.Handled = true;
            return;
        }
        if (_info == null || Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(-1, shift); break;
            case Key.Right: Step(1, shift); break;
            case Key.I: SetStart_Click(); break;
            case Key.O: SetEnd_Click(); break;
            default: return;
        }
        e.Handled = true;
    }

    // Un clic en dehors du champ en cours de saisie le valide et rend les raccourcis (Espace…) à la fenêtre
    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox tb) return;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d != null;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == tb) return;
        Focus();
    }

    // ================================================================ ROI
    Int32Rect? GetCrop()
    {
        var r = Roi.NormalizedRoi;
        if (_info == null || r.IsEmpty) return null;

        // Dimensions et position paires (exigence du chroma 4:2:0)
        int x = (int)Math.Round(r.X * EffW) & ~1;
        int y = (int)Math.Round(r.Y * EffH) & ~1;
        int w = (int)Math.Round(r.Width * EffW) & ~1;
        int h = (int)Math.Round(r.Height * EffH) & ~1;
        w = Math.Min(w, EffW - x);
        h = Math.Min(h, EffH - y);
        w &= ~1; h &= ~1;
        if (w < 16 || h < 16) return null;
        if (w >= EffW && h >= EffH) return null;
        return new Int32Rect(x, y, w, h);
    }

    void OnRoiChanged(object? sender, EventArgs e)
    {
        if (_info == null) return;
        UpdateRoiFields(GetCrop());
        RefreshGeometry();
    }

    // ================================================================ Rotation / redimensionnement
    bool NeedsReencode() => GetCrop() != null || _userRot != 0 || GetOutputSize().Resized;

    static int Even(double v) => Math.Max(16, (int)Math.Round(v / 2) * 2);

    /// <summary>Taille finale : image affichée (ou ROI) puis redimensionnement en conservant le ratio.</summary>
    (int W, int H, bool Resized) GetOutputSize()
    {
        if (_info == null) return (0, 0, false);
        var crop = GetCrop();
        int bw = crop?.Width ?? EffW, bh = crop?.Height ?? EffH;
        int w, h;
        switch (_scaleMode)
        {
            case ScaleMode.Percent: w = Even(bw * _scaleValue / 100); h = Even(bh * _scaleValue / 100); break;
            case ScaleMode.Width: w = Even(_scaleValue); h = Even(_scaleValue * bh / bw); break;
            case ScaleMode.Height: h = Even(_scaleValue); w = Even(_scaleValue * bw / bh); break;
            default: return (bw, bh, false);
        }
        return (w, h, w != bw || h != bh);
    }

    void UpdateResizeFields()
    {
        if (_info == null) return;
        var (w, h, _) = GetOutputSize();
        if (!OutWBox.IsKeyboardFocused) OutWBox.Text = w.ToString();
        if (!OutHBox.IsKeyboardFocused) OutHBox.Text = h.ToString();
    }

    /// <summary>À appeler après tout changement de ROI, rotation ou redimensionnement.</summary>
    void RefreshGeometry()
    {
        if (_info == null) return;
        UpdateResizeFields();

        // Rotation / ROI / redimensionnement imposent un ré-encodage ; sinon on revient à la copie directe
        bool needs = NeedsReencode();
        if (EncoderBox.SelectedItem is EncoderItem cur)
        {
            if (needs && cur.Name == "copy") EncoderBox.SelectedIndex = 1;
            else if (!needs && cur.Name == "auto") EncoderBox.SelectedIndex = 0;
        }
        UpdateTarget();
    }

    void Rotation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        int newRot = RotationBox.SelectedIndex * 90;
        if (_info == null) { _userRot = newRot; return; }
        if (newRot == _userRot) return;

        int delta = (newRot - _userRot + 360) % 360;
        var old = Roi.NormalizedRoi;
        _userRot = newRot;
        StopPlayback();
        Roi.Aspect = (double)EffW / EffH;
        Roi.ResetZoom();

        // La ROI suit la rotation de l'image
        if (!old.IsEmpty)
        {
            Rect m = delta switch
            {
                90 => new Rect(1 - old.Y - old.Height, old.X, old.Height, old.Width),
                180 => new Rect(1 - old.X - old.Width, 1 - old.Y - old.Height, old.Width, old.Height),
                _ => new Rect(old.Y, 1 - old.X - old.Width, old.Height, old.Width)   // 270 (= -90)
            };
            Roi.SetRoi(m);
        }
        else RefreshGeometry();

        UpdateRoiFields(GetCrop());
        RequestFrame();
    }

    void Resize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !IsLoaded || _info == null) return;
        switch (ResizeBox.SelectedIndex)
        {
            case 0: _scaleMode = ScaleMode.None; break;
            case 1: _scaleMode = ScaleMode.Percent; _scaleValue = 75; break;
            case 2: _scaleMode = ScaleMode.Percent; _scaleValue = 50; break;
            case 3: _scaleMode = ScaleMode.Percent; _scaleValue = 25; break;
            default: return; // « Personnalisée » : on garde la valeur courante
        }
        RefreshGeometry();
    }

    void OutW_LostFocus(object sender, RoutedEventArgs e) => ApplyOutSize(true);
    void OutH_LostFocus(object sender, RoutedEventArgs e) => ApplyOutSize(false);

    // Saisie d'une largeur OU d'une hauteur : l'autre dimension est déduite (ratio conservé)
    void ApplyOutSize(bool isWidth)
    {
        if (_info == null) return;
        var tb = isWidth ? OutWBox : OutHBox;
        if (int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 16)
        {
            var cur = GetOutputSize();
            if (v != (isWidth ? cur.W : cur.H))
            {
                _scaleMode = isWidth ? ScaleMode.Width : ScaleMode.Height;
                _scaleValue = Math.Min(v, 16384);
                _suppress = true;
                ResizeBox.SelectedIndex = 4; // « Personnalisée »
                _suppress = false;
            }
        }
        RefreshGeometry();
    }

    void UpdateRoiFields(Int32Rect? crop)
    {
        static void Set(TextBox tb, int? v) { if (!tb.IsKeyboardFocused) tb.Text = v?.ToString() ?? ""; }
        Set(RoiXBox, crop?.X);
        Set(RoiYBox, crop?.Y);
        Set(RoiWBox, crop?.Width);
        Set(RoiHBox, crop?.Height);
    }

    // Saisie manuelle de la ROI : les champs laissés vides gardent la valeur actuelle
    // (ou l'image entière si aucune ROI n'existe encore).
    void RoiField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var cur = GetCrop() ?? new Int32Rect(0, 0, EffW, EffH);

        static int Parse(TextBox tb, int fallback) =>
            int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        int x = Parse(RoiXBox, cur.X), y = Parse(RoiYBox, cur.Y);
        int w = Parse(RoiWBox, cur.Width), h = Parse(RoiHBox, cur.Height);

        if (x != cur.X || y != cur.Y || w != cur.Width || h != cur.Height)
        {
            x = Math.Clamp(x, 0, EffW - 16);
            y = Math.Clamp(y, 0, EffH - 16);
            w = Math.Clamp(w, 16, EffW - x);
            h = Math.Clamp(h, 16, EffH - y);
            Roi.SetRoi(new Rect((double)x / EffW, (double)y / EffH,
                                (double)w / EffW, (double)h / EffH));
        }
        UpdateRoiFields(GetCrop()); // reformate / restaure (valeurs paires)
    }

    void ClearRoi_Click(object sender, RoutedEventArgs e) => Roi.ClearRoi();

    // ================================================================ Options
    void Options_Changed(object sender, SelectionChangedEventArgs e) => UpdateTarget();
    void ValueBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTarget();

    double ParseValue() =>
        double.TryParse(ValueBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v : 20;

    void UpdateTarget()
    {
        if (!IsLoaded || _info == null || EncoderBox.SelectedItem is not EncoderItem enc) return;

        bool copy = enc.Name == "copy";
        BitrateModeBox.IsEnabled = !copy;
        if (copy)
        {
            ValueBox.Visibility = ValueLabel.Visibility = Visibility.Collapsed;
            TargetText.Text = "Copie directe : pas de perte, coupe sur image clé (début/fin approximatifs)";
            return;
        }

        var (ow, oh, _) = GetOutputSize();
        var mode = (RateMode)BitrateModeBox.SelectedIndex;
        bool showValue = mode is RateMode.CustomMbps or RateMode.Quality;
        ValueBox.Visibility = ValueLabel.Visibility = showValue ? Visibility.Visible : Visibility.Collapsed;
        ValueLabel.Text = mode == RateMode.Quality ? "CRF / CQ (bas = meilleur)" : "Mbps";

        var k = FfmpegService.TargetBitrateKbps(_info, ow, oh, mode, ParseValue());
        TargetText.Text = $"Sortie {ow}×{oh} — " + (k is { } v
            ? $"débit cible ≈ {v / 1000.0:0.#} Mbps"
            : mode == RateMode.Quality ? "qualité constante" : "débit source inconnu → qualité constante (CRF 20)");
    }

    // ================================================================ Export / Annuler (un seul bouton)
    void ExportCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) _exportCts?.Cancel();
        else Export_Click(sender, e);
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) { MessageBox.Show("Ouvrez d'abord une vidéo."); return; }
        if (EncoderBox.SelectedItem is not EncoderItem enc) return;
        StopPlayback();
        if (_end - _start < 0.05) { MessageBox.Show("La plage début/fin est trop courte."); return; }

        var crop = GetCrop();
        var (outW, outH, resized) = GetOutputSize();
        if (enc.Name == "copy" && NeedsReencode())
        {
            MessageBox.Show("Le recadrage, la rotation et le redimensionnement nécessitent un ré-encodage : choisissez un encodeur.");
            return;
        }

        string encoder = enc.Name == "auto" ? FfmpegService.ResolveAuto(_info, _available) : enc.Name;

        var dlg = new SaveFileDialog
        {
            Filter = "MP4|*.mp4",
            FileName = UniqueFileName(System.IO.Path.GetDirectoryName(_info.Path) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(_info.Path) + (crop != null ? "_crop" : NeedsReencode() ? "_edit" : "_cut"),
                ".mp4"),
            InitialDirectory = System.IO.Path.GetDirectoryName(_info.Path)
        };
        if (dlg.ShowDialog() != true) return;
        if (string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(_info.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Le fichier de sortie ne peut pas être le fichier source.");
            return;
        }

        var job = new ExportJob(_info, dlg.FileName, _start, _end, crop, encoder,
            (RateMode)BitrateModeBox.SelectedIndex, ParseValue(), AudioCheck.IsChecked == true,
            _userRot, resized ? outW : 0, resized ? outH : 0);

        _exportCts = new CancellationTokenSource();
        SetBusy(true);
        Bar.Value = 0;
        var progress = new Progress<double>(p =>
        {
            Bar.Value = p * 100;
            Status.Text = $"Export… {p:P0}";
            Title = $"{p * 100:0} % — {AppTitle}";      // visible dans la barre de tâche
            TaskbarInfo.ProgressValue = p;
        });
        Title = $"0 % — {AppTitle}";
        TaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
        TaskbarInfo.ProgressValue = 0;

        try
        {
            await FfmpegService.ExportAsync(job, progress, _exportCts.Token);
            double mb = new FileInfo(job.Output).Length / 1048576.0;
            Status.Text = $"Terminé ({mb:0.#} Mo)";
            Title = $"Terminé — {AppTitle}";
            var answer = MessageBox.Show(
                $"Export terminé ({mb:0.#} Mo) :\n{job.Output}\n\nOuvrir le dossier ?",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) OpenInExplorer(job.Output);
        }
        catch (OperationCanceledException) { Status.Text = "Export annulé"; Bar.Value = 0; Title = AppTitle; }
        catch (Exception ex)
        {
            Status.Text = "Erreur";
            Title = AppTitle;
            MessageBox.Show(ex.Message, "Erreur d'export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
            SetBusy(false);
        }
    }

    /// <summary>Nom de fichier inexistant dans le dossier : nom.mp4, sinon nom_2.mp4, nom_3.mp4…</summary>
    static string UniqueFileName(string folder, string baseName, string ext)
    {
        string name = baseName + ext;
        for (int i = 2; File.Exists(System.IO.Path.Combine(folder, name)); i++)
            name = $"{baseName}_{i}{ext}";
        return name;
    }

    static void OpenInExplorer(string file)
    {
        try
        {
            // Ouvre l'Explorateur avec le fichier sélectionné
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    void SetBusy(bool busy)
    {
        _exporting = busy;
        WorkArea.IsEnabled = !busy;                       // le bouton Exporter/Annuler est hors de WorkArea
        ExportBtn.Content = busy ? "Annuler" : "Exporter…";
    }
}
