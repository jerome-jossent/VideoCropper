using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using VideoCropper.Models;

namespace VideoCropper.Services;

public static class FfmpegService
{
    public static string FfmpegPath { get; } = Locate("ffmpeg.exe");
    public static string FfprobePath { get; } = Locate("ffprobe.exe");

    static string Locate(string exe)
    {
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        return File.Exists(local) ? local : exe; // sinon : PATH
    }

    static string Inv(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static async Task<(int Code, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args)
    {
        using var p = Process.Start(Psi(exe, args))!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o, await e);
    }

    // ---------------------------------------------------------------- Probe
    static string? S(JsonElement e, string name) =>
        e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty(name, out var p)
            ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString())
            : null;

    static double D(JsonElement e, string name) =>
        double.TryParse(S(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    static double ParseRate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public static async Task<VideoInfo> ProbeAsync(string path)
    {
        var (code, json, err) = await RunAsync(FfprobePath,
            new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path });
        if (code != 0) throw new InvalidOperationException("ffprobe a échoué :\n" + err);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement v = default, a = default;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = S(s, "codec_type");
            if (type == "video" && v.ValueKind == JsonValueKind.Undefined) v = s;
            else if (type == "audio" && a.ValueKind == JsonValueKind.Undefined) a = s;
        }
        if (v.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Aucun flux vidéo trouvé.");

        var format = root.GetProperty("format");
        double duration = D(format, "duration");
        if (duration <= 0) duration = D(v, "duration");

        double fps = ParseRate(S(v, "avg_frame_rate"));
        if (fps <= 0) fps = ParseRate(S(v, "r_frame_rate"));
        if (fps <= 0) fps = 30;

        double br = D(v, "bit_rate");
        if (br <= 0)
        {
            double total = D(format, "bit_rate");
            double audio = a.ValueKind != JsonValueKind.Undefined ? D(a, "bit_rate") : 0;
            br = Math.Max(0, total - audio);
        }

        static string? Known(string? s) => string.IsNullOrEmpty(s) || s == "unknown" ? null : s;

        return new VideoInfo(
            path,
            S(v, "codec_name") ?? "hevc",
            S(v, "pix_fmt") ?? "yuv420p",
            (int)D(v, "width"), (int)D(v, "height"),
            fps, duration, (long)(br / 1000),
            a.ValueKind != JsonValueKind.Undefined,
            Known(S(v, "color_primaries")), Known(S(v, "color_transfer")), Known(S(v, "color_space")),
            ReadRotation(v),
            ReadCreationTime(format, v));
    }

    /// <summary>
    /// Rotation à appliquer à l'affichage, en degrés horaires (0/90/180/270).
    /// Lit la matrice d'affichage (ffprobe donne l'angle anti-horaire) ou, à défaut, le tag "rotate".
    /// </summary>
    static int ReadRotation(JsonElement v)
    {
        double? deg = null;
        if (v.TryGetProperty("side_data_list", out var sd) && sd.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sd.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out _) &&
                    double.TryParse(S(item, "rotation"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                {
                    deg = -r;
                    break;
                }
            }
        }
        if (deg == null && v.TryGetProperty("tags", out var tags) &&
            double.TryParse(S(tags, "rotate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            deg = t;

        if (deg == null) return 0;
        int q = (int)(Math.Round(deg.Value / 90.0) * 90);
        return ((q % 360) + 360) % 360;
    }

    /// <summary>Date de début d'enregistrement (métadonnée creation_time), en UTC, ou null.</summary>
    static DateTime? ReadCreationTime(JsonElement format, JsonElement v)
    {
        string? s = null;
        if (format.TryGetProperty("tags", out var ft)) s = S(ft, "creation_time");
        if (string.IsNullOrEmpty(s) && v.TryGetProperty("tags", out var vt)) s = S(vt, "creation_time");
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)) return null;
        return dto.Year < 1990 ? null : dto.UtcDateTime; // certaines caméras écrivent 1970 / 1904 quand l'horloge n'est pas réglée
    }

    /// <summary>Filtre FFmpeg pour une rotation horaire de 0/90/180/270°.</summary>
    static string? RotateFilter(int rotation) => (((rotation % 360) + 360) % 360) switch
    {
        90 => "transpose=1",
        180 => "transpose=1,transpose=1",
        270 => "transpose=2",
        _ => null
    };

    static string Chain(params string?[] parts) => string.Join(",", parts.Where(p => !string.IsNullOrEmpty(p)));

    // ---------------------------------------------------------------- Aperçu
    /// <param name="fast">Seek approximatif (image clé la plus proche, sans décoder jusqu'à la position exacte) : très rapide, idéal pendant le glissement.</param>
    public static async Task<BitmapSource> ExtractFrameAsync(string path, double t, int width, int rotation, bool fast, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (fast) args.Add("-noaccurate_seek");
        args.AddRange(new[]
        {
            "-ss", Inv(t), "-i", path,
            "-frames:v", "1", "-an",
            "-vf", Chain(RotateFilter(rotation), $"scale={width}:-2"),
            "-f", "image2pipe", "-c:v", "bmp", "pipe:1"
        });
        using var p = Process.Start(Psi(FfmpegPath, args))!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        if (ms.Length == 0) throw new InvalidOperationException("Extraction d'image impossible :\n" + await errTask);

        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ---------------------------------------------------------------- Lecture
    /// <summary>
    /// Décode la vidéo en continu (basse résolution, sans audio) à partir de startT et appelle onFrame
    /// au rythme réel. onFrame est appelé dans le contexte de l'appelant (thread UI).
    /// Se termine à la fin de la vidéo ou à l'annulation.
    /// </summary>
    public static async Task PlayAsync(VideoInfo info, double startT, int width, int rotation,
        Action<byte[], int, int, double> onFrame, CancellationToken ct)
    {
        var (ow, oh) = info.Oriented(rotation);
        int height = Math.Max(2, (int)Math.Round(width * (double)oh / ow / 2) * 2);
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-hwaccel", "auto",
            "-ss", Inv(startT), "-i", info.Path,
            "-an", "-vf", Chain(RotateFilter(rotation), $"scale={width}:{height}"),
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"
        };
        using var p = Process.Start(Psi(FfmpegPath, args))!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        _ = p.StandardError.ReadToEndAsync(); // vider stderr

        var stream = p.StandardOutput.BaseStream;
        int frameSize = width * height * 3;
        var buf = new byte[frameSize];
        var clock = Stopwatch.StartNew();
        long n = 0;

        while (!ct.IsCancellationRequested)
        {
            int read = 0;
            while (read < frameSize)
            {
                int r = await stream.ReadAsync(buf, read, frameSize - read, ct);
                if (r == 0) return; // fin de la vidéo
                read += r;
            }

            double target = n / info.Fps;
            double wait = target - clock.Elapsed.TotalSeconds;
            if (wait > 0.002) await Task.Delay(TimeSpan.FromSeconds(wait), ct);

            onFrame(buf, width, height, startT + target);
            n++;
        }
    }

    // ---------------------------------------------------------------- Encodeurs
    public static async Task<List<string>> DetectEncodersAsync()
    {
        var (_, all, _) = await RunAsync(FfmpegPath, new[] { "-hide_banner", "-encoders" });
        var software = new[] { "libx265", "libx264" };
        var hardware = new[] { "hevc_nvenc", "h264_nvenc", "hevc_qsv", "h264_qsv", "hevc_amf", "h264_amf" };

        var result = software.Where(e => all.Contains(" " + e + " ")).ToList();

        // Les encodeurs matériels sont listés même sans GPU compatible : on les teste réellement.
        var tests = hardware.Where(e => all.Contains(" " + e + " ")).Select(async e =>
        {
            var (code, _, _) = await RunAsync(FfmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=black:s=640x360:d=0.2",
                "-c:v", e, "-f", "null", "-"
            });
            return code == 0 ? e : null;
        }).ToList();
        foreach (var r in await Task.WhenAll(tests))
            if (r != null) result.Add(r);
        return result;
    }

    static bool IsHevc(string e) => e.Contains("265") || e.Contains("hevc");

    public static string ResolveAuto(VideoInfo info, IReadOnlyCollection<string> available)
    {
        bool h264 = info.VCodec == "h264";
        string want = h264 ? "libx264" : "libx265";
        if (available.Contains(want)) return want;
        return available.FirstOrDefault(e => IsHevc(e) != h264) ?? available.FirstOrDefault() ?? want;
    }

    // ---------------------------------------------------------------- Débit
    public static long? TargetBitrateKbps(VideoInfo i, int outW, int outH, RateMode mode, double value)
    {
        switch (mode)
        {
            case RateMode.Quality: return null;
            case RateMode.CustomMbps: return (long)(value * 1000);
            case RateMode.Same: return i.BitrateKbps > 0 ? i.BitrateKbps : null;
            default:
                if (i.BitrateKbps <= 0) return null;
                double ratio = (double)outW * outH / ((double)i.Width * i.Height);
                return Math.Max(1500, (long)(i.BitrateKbps * ratio));
        }
    }

    static (int W, int H) OutputSize(ExportJob j) =>
        j.OutW > 0 && j.OutH > 0 ? (j.OutW, j.OutH)
        : j.Crop is { } c ? (c.Width, c.Height)
        : (j.Info.Width, j.Info.Height);

    // ---------------------------------------------------------------- Export
    public static List<string> BuildArgs(ExportJob j)
    {
        var a = new List<string>
        {
            "-y", "-hide_banner", "-nostats", "-progress", "pipe:1",
            "-ss", Inv(j.Start), "-i", j.Info.Path, "-t", Inv(j.End - j.Start),
            "-map", "0:v:0"
        };
        if (j.KeepAudio && j.Info.HasAudio) { a.Add("-map"); a.Add("0:a?"); a.AddRange(new[] { "-c:a", "copy" }); }
        else a.Add("-an");
        a.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart" });

        if (j.Encoder == "copy")
        {
            a.AddRange(new[] { "-c:v", "copy", "-avoid_negative_ts", "make_zero" });
            if (j.Info.VCodec == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" });
        }
        else
        {
            string enc = j.Encoder;

            // FFmpeg applique d'abord seul la rotation des métadonnées (autorotate) ; ensuite :
            // rotation choisie → recadrage (coordonnées de l'image affichée) → redimensionnement.
            var vf = new List<string>();
            var rf = RotateFilter(j.Rotation);
            if (rf != null) vf.Add(rf);
            if (j.Crop is { } c) vf.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
            if (j.OutW > 0 && j.OutH > 0) vf.Add($"scale={j.OutW}:{j.OutH}:flags=lanczos");
            if (vf.Count > 0) a.AddRange(new[] { "-vf", string.Join(",", vf) });
            // L'image est déjà redressée : on évite toute double rotation à la lecture
            a.AddRange(new[] { "-metadata:s:v:0", "rotate=0" });

            a.AddRange(new[] { "-c:v", enc });

            bool nvenc = enc.EndsWith("_nvenc"), qsv = enc.EndsWith("_qsv"), amf = enc.EndsWith("_amf");
            bool hw = nvenc || qsv || amf;
            bool h264 = enc.Contains("264");
            bool ten = j.Info.PixFmt.Contains("10");

            // Format de pixel
            string pix;
            if (h264 && ten) pix = "yuv420p";
            else if (hw) pix = ten ? "p010le" : (nvenc ? "yuv420p" : "nv12");
            else pix = j.Info.PixFmt.StartsWith("yuv420p") ? j.Info.PixFmt : "yuv420p";
            a.AddRange(new[] { "-pix_fmt", pix });

            // Débit / qualité
            var (ow, oh) = OutputSize(j);
            long? kbps = TargetBitrateKbps(j.Info, ow, oh, j.Mode, j.Value);
            bool quality = j.Mode == RateMode.Quality || kbps == null;
            double q = j.Mode == RateMode.Quality ? j.Value : 20;
            string qs = q.ToString("0.#", CultureInfo.InvariantCulture);

            if (nvenc)
            {
                a.AddRange(new[] { "-preset", "p5" });
                if (quality) a.AddRange(new[] { "-rc", "vbr", "-cq", qs, "-b:v", "0" });
                else a.AddRange(new[] { "-b:v", kbps + "k" });
            }
            else if (qsv)
            {
                if (quality) a.AddRange(new[] { "-global_quality", qs });
                else a.AddRange(new[] { "-b:v", kbps + "k" });
            }
            else if (amf)
            {
                if (quality) a.AddRange(new[] { "-rc", "cqp", "-qp_i", qs, "-qp_p", qs, "-qp_b", qs });
                else a.AddRange(new[] { "-b:v", kbps + "k" });
            }
            else
            {
                a.AddRange(new[] { "-preset", "medium" });
                if (quality) a.AddRange(new[] { "-crf", qs });
                else a.AddRange(new[] { "-b:v", kbps + "k" });
            }

            if (IsHevc(enc)) a.AddRange(new[] { "-tag:v", "hvc1" });

            // Conserver les informations colorimétriques (HLG, BT.2020, …)
            if (j.Info.ColorPrimaries != null) a.AddRange(new[] { "-color_primaries", j.Info.ColorPrimaries });
            if (j.Info.ColorTransfer != null) a.AddRange(new[] { "-color_trc", j.Info.ColorTransfer });
            if (j.Info.ColorSpace != null) a.AddRange(new[] { "-colorspace", j.Info.ColorSpace });
        }

        // Date de début interne du clip = début d'origine + temps retiré au début
        if (j.Info.CreationTimeUtc is DateTime created)
            a.AddRange(new[]
            {
                "-metadata",
                "creation_time=" + created.AddSeconds(j.Start).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)
            });

        a.Add(j.Output);
        return a;
    }

    public static async Task ExportAsync(ExportJob job, IProgress<double> progress, CancellationToken ct)
    {
        var args = BuildArgs(job);
        double total = Math.Max(0.001, job.End - job.Start);

        using var p = Process.Start(Psi(FfmpegPath, args))!;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });

        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us))
                progress.Report(Math.Clamp(us / 1e6 / total, 0, 1));
        }
        await p.WaitForExitAsync();

        if (ct.IsCancellationRequested)
        {
            TryDelete(job.Output);
            throw new OperationCanceledException(ct);
        }
        if (p.ExitCode != 0)
        {
            TryDelete(job.Output);
            string s; lock (err) s = err.ToString();
            throw new InvalidOperationException("FFmpeg a échoué :\n" + (s.Length > 1500 ? s[^1500..] : s));
        }
        ApplySourceTimestamps(job);
        progress.Report(1);
    }

    /// <summary>
    /// Donne au fichier créé la date/heure du fichier source, corrigée du temps retiré.
    ///  - date du fichier = DÉBUT de la vidéo  → source + temps retiré au début
    ///  - date du fichier = FIN de la vidéo    → source − temps retiré à la fin
    /// (appareils photo / caméras : la date de modification est en général celle de la fin d'enregistrement)
    /// Le cas est détecté avec la métadonnée creation_time ; sans elle, on suppose que la date = début.
    /// </summary>
    static void ApplySourceTimestamps(ExportJob job)
    {
        try
        {
            var info = job.Info;
            DateTime mtime = File.GetLastWriteTimeUtc(info.Path);
            double shiftSeconds = FileDateIsEnd(info, mtime) ? job.End - info.Duration : job.Start;
            DateTime t = mtime.AddSeconds(shiftSeconds);
            File.SetCreationTimeUtc(job.Output, t);
            File.SetLastWriteTimeUtc(job.Output, t);
        }
        catch { /* la date du fichier n'est pas critique */ }
    }

    static bool FileDateIsEnd(VideoInfo info, DateTime mtimeUtc)
    {
        if (info.CreationTimeUtc is not DateTime start) return false;
        double dStart = Math.Abs((mtimeUtc - start).TotalSeconds);
        double dEnd = Math.Abs((mtimeUtc - start.AddSeconds(info.Duration)).TotalSeconds);
        return dEnd <= 300 && dEnd < dStart; // proche (5 min) de la fin d'enregistrement et plus proche de la fin que du début
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
