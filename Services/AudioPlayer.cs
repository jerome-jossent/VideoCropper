using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VideoCropper.Services;

/// <summary>
/// Lecture de la piste audio d'une vidéo : FFmpeg décode en PCM 48 kHz stéréo, NAudio le joue.
/// Sert aussi d'horloge maîtresse pour synchroniser l'image sur le son (voir FfmpegService.PlayAsync).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    const int Rate = 48000, Channels = 2, BytesPerSample = 2;
    const int FrameBytes = Channels * BytesPerSample;
    const int BytesPerSecond = Rate * FrameBytes;

    readonly Process _proc;
    readonly BufferedWaveProvider _buffer;
    readonly WaveOutEvent _out;
    readonly CancellationTokenSource _cts = new();
    volatile bool _ended;     // flux FFmpeg terminé
    bool _playing, _disposed;

    /// <summary>Volume 0..1.</summary>
    public double Volume
    {
        get => _out.Volume;
        set => _out.Volume = (float)Math.Clamp(value, 0, 1);
    }

    /// <summary>Temps réellement joué depuis le démarrage de la sortie, en secondes.</summary>
    public double Position => _playing ? _out.GetPosition() / (double)BytesPerSecond : 0;

    public AudioPlayer(string ffmpegPath, string file, double startSeconds)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-ss", startSeconds.ToString("0.000", CultureInfo.InvariantCulture), "-i", file,
            "-vn", "-sn", "-dn",
            "-f", "s16le", "-ac", Channels.ToString(), "-ar", Rate.ToString(), "pipe:1"
        }) psi.ArgumentList.Add(a);

        _proc = Process.Start(psi)!;
        _ = _proc.StandardError.ReadToEndAsync();

        _buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = false,
            ReadFully = true            // silence si le tampon est vide : l'horloge continue d'avancer
        };
        _out = new WaveOutEvent { DesiredLatency = 100 };
        _out.Init(_buffer);

        _ = Task.Run(PumpAsync);        // décode et remplit le tampon dès maintenant
    }

    async Task PumpAsync()
    {
        var ct = _cts.Token;
        var stream = _proc.StandardOutput.BaseStream;
        var buf = new byte[BytesPerSecond / 20 + FrameBytes]; // ≈ 50 ms
        int have = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Ne pas décoder plus de ~1,5 s à l'avance
                while (_buffer.BufferedBytes > BytesPerSecond * 3 / 2) await Task.Delay(20, ct);

                int n = await stream.ReadAsync(buf, have, buf.Length - have, ct);
                if (n == 0) break;
                have += n;

                // On ne donne au tampon que des trames complètes (sinon les canaux se décalent)
                int usable = have - have % FrameBytes;
                _buffer.AddSamples(buf, 0, usable);
                Buffer.BlockCopy(buf, usable, buf, 0, have - usable);
                have -= usable;
            }
        }
        catch { /* annulation / processus tué */ }
        finally { _ended = true; }
    }

    /// <summary>Attend un petit pré-tampon puis lance la sortie sonore (Position démarre à 0).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (_buffer.BufferedBytes < BytesPerSecond * 15 / 100 && !_ended && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(10, ct);
        _out.Play();
        _playing = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _proc.Kill(true); } catch { }
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
        try { _proc.Dispose(); } catch { }
    }
}
