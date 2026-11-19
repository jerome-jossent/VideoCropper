using System.Windows;

namespace VideoCropper.Models;

public enum RateMode { Proportional, Same, CustomMbps, Quality }

/// <summary>Mode de redimensionnement (ratio toujours conservé).</summary>
public enum ScaleMode { None, Percent, Width, Height }

public record EncoderItem(string Label, string Name);

public record VideoInfo(
    string Path,
    string VCodec,
    string PixFmt,
    int Width,              // dimensions « codées » (avant rotation des métadonnées)
    int Height,
    double Fps,
    double Duration,
    long BitrateKbps,
    bool HasAudio,
    string? ColorPrimaries,
    string? ColorTransfer,
    string? ColorSpace,
    int Rotation)           // rotation des métadonnées, en degrés horaires à appliquer à l'affichage (0/90/180/270)
{
    /// <summary>Dimensions telles qu'affichées par un lecteur (rotation des métadonnées appliquée).</summary>
    public int DisplayWidth => Rotation % 180 == 0 ? Width : Height;
    public int DisplayHeight => Rotation % 180 == 0 ? Height : Width;

    /// <summary>Dimensions après une rotation supplémentaire choisie par l'utilisateur.</summary>
    public (int W, int H) Oriented(int userRotation) =>
        userRotation % 180 == 0 ? (DisplayWidth, DisplayHeight) : (DisplayHeight, DisplayWidth);
}

public record ExportJob(
    VideoInfo Info,
    string Output,
    double Start,
    double End,
    Int32Rect? Crop,        // exprimé dans l'orientation finale (après rotation), avant redimensionnement
    string Encoder,         // "copy" ou nom d'encodeur FFmpeg
    RateMode Mode,
    double Value,
    bool KeepAudio,
    int Rotation,           // rotation supplémentaire (0/90/180/270 horaire)
    int OutW,               // taille de sortie après redimensionnement (0 = pas de redimensionnement)
    int OutH);
