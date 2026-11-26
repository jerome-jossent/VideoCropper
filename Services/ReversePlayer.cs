using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VideoCropper.Models;

namespace VideoCropper.Services;

/// <summary>
/// Lecture à l'envers à vitesse normale (sans son).
/// FFmpeg décode de petits blocs d'images qu'il inverse avec le filtre « reverse » ; le bloc précédent est
/// préparé pendant l'affichage du bloc courant. Le 1er bloc est très court et son décodage démarre dès la
/// construction de l'objet (à l'appui sur le bouton), pour que le rembobinage démarre sans attendre.
/// </summary>
public sealed class ReversePlayer : IDisposable
{
    const int Width = 640; // aperçu léger pour tenir le temps réel

    readonly VideoInfo _info;
    readonly int _rotation, _w, _h;
    readonly double _startT, _frameDur, _bigChunk;
    readonly CancellationTokenSource _cts = new();
    Task<List<byte[]>>? _next;
    double _end, _len;

    public bool CanPlay => _next != null;

    public ReversePlayer(VideoInfo info, double startT, int rotation)
    {
        _info = info;
        _rotation = rotation;
        _startT = startT;
        var (ow, oh) = info.Oriented(rotation);
        _w = Width;
        _h = Math.Max(2, (int)Math.Round(_w * (double)oh / ow / 2) * 2);
        _frameDur = 1.0 / info.Fps;
        _bigChunk = Math.Min(1.5, 90 * _frameDur);   // ≈ 1,5 s (90 images maximum)
        _end = startT;
        _len = Math.Min(0.3, _bigChunk);             // 1er bloc court : démarrage rapide
        if (_end > 0.0005) _next = DecodeAsync(Math.Max(0, _end - _len), _end);
    }

    /// <summary>Affiche les images en remontant le temps jusqu'à l'annulation ou le début de la vidéo.</summary>
    public async Task RunAsync(Action<byte[], int, int, double> onFrame, CancellationToken ct)
    {
        var clock = new Stopwatch();
        double anchorClock = 0, anchorT = _startT;   // à l'instant anchorClock, on affiche la position anchorT
        bool first = true;
        List<byte[]>? frames = null;
        try
        {
            while (_next != null)
            {
                double chunkFrom = Math.Max(0, _end - _len);
                var task = _next;
                _next = null;

                frames = await task.WaitAsync(ct);

                // Prépare le bloc précédent pendant qu'on affiche celui-ci
                _end = chunkFrom;
                _len = _bigChunk;
                if (_end > 0.0005) _next = DecodeAsync(Math.Max(0, _end - _len), _end);

                int m = frames.Count;
                if (m > 0)
                {
                    double ft0 = chunkFrom + (m - 1) * _frameDur; // 1re image affichée de ce bloc (la plus récente)
                    if (first)
                    {
                        clock.Restart();
                        first = false;
                        anchorClock = 0;
                        anchorT = _startT;
                    }
                    else
                    {
                        double due0 = anchorClock + (anchorT - ft0);
                        double now = clock.Elapsed.TotalSeconds;
                        if (now - due0 > 0.1)
                        {
                            // Le bloc est arrivé en retard : on reprend là où on en est, sans sauter dans la vidéo
                            anchorClock = now;
                            anchorT = ft0;
                        }
                    }

                    for (int k = 0; k < m; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        double ft = chunkFrom + (m - 1 - k) * _frameDur; // les images sortent dans l'ordre inverse
                        double due = anchorClock + (anchorT - ft);
                        if (clock.Elapsed.TotalSeconds - due > 0.25) continue; // trop en retard : on saute cette image

                        double wait;
                        while ((wait = due - clock.Elapsed.TotalSeconds) > 0.002)
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 0.02)), ct);

                        onFrame(frames[k], _w, _h, ft);
                    }
                }
                foreach (var f in frames) ArrayPool<byte>.Shared.Return(f);
                frames = null;
            }
        }
        finally
        {
            if (frames != null) foreach (var f in frames) ArrayPool<byte>.Shared.Return(f);
        }
    }

    async Task<List<byte[]>> DecodeAsync(double from, double to)
    {
        var ct = _cts.Token;
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-hwaccel", "auto",
            "-ss", FfmpegService.Inv(from), "-i", _info.Path, "-t", FfmpegService.Inv(to - from),
            "-an",
            "-vf", FfmpegService.Chain(FfmpegService.RotateFilter(_rotation), $"scale={_w}:{_h}", "reverse"),
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"
        };
        using var p = Process.Start(FfmpegService.Psi(FfmpegService.FfmpegPath, args))!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync();

        var stream = p.StandardOutput.BaseStream;
        int frameSize = _w * _h * 3;
        var frames = new List<byte[]>();
        try
        {
            while (true)
            {
                var buf = ArrayPool<byte>.Shared.Rent(frameSize);
                int read = 0;
                while (read < frameSize)
                {
                    int r = await stream.ReadAsync(buf, read, frameSize - read, ct);
                    if (r == 0) break;
                    read += r;
                }
                if (read < frameSize) { ArrayPool<byte>.Shared.Return(buf); break; }
                frames.Add(buf);
            }
        }
        catch
        {
            foreach (var f in frames) ArrayPool<byte>.Shared.Return(f);
            throw;
        }

        // Aucune image alors que le bloc n'était pas minuscule : on remonte l'erreur de FFmpeg au lieu de rester muet
        if (frames.Count == 0 && !ct.IsCancellationRequested && (to - from) >= 2 * _frameDur)
        {
            await p.WaitForExitAsync(ct);
            string err = (await errTask).Trim();
            if (err.Length > 0) throw new InvalidOperationException(err.Length > 600 ? err[^600..] : err);
        }
        return frames;
    }

    public void Dispose()
    {
        _cts.Cancel(); // tue les processus FFmpeg en cours
        if (_next != null) _ = _next.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
    }
}
