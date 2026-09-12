// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Graphics;
using SharpProspero.Interop.Sysmodule;
using SharpProspero.Modules;
using SharpProspero.Storage;
using System;

namespace ProsperoExplorer.Images;

/// <summary>What a picture file holds. This covers the forms that are read; only some are written.</summary>
internal enum ImageForm
{
    /// <summary>Neither the name nor the leading bytes named a form that is read.</summary>
    Unknown,

    /// <summary>Lossless, with an alpha channel.</summary>
    Png,

    /// <summary>Lossy, no alpha channel.</summary>
    Jpeg,

    /// <summary>Uncompressed, no alpha channel.</summary>
    Bmp,

    /// <summary>Uncompressed or run-length encoded, with an alpha channel.</summary>
    Tga,

    /// <summary>Palette based, and able to hold more than one frame.</summary>
    Gif,
}

/// <summary>
/// A picture that has been decoded, and the pixels behind it. A surface handed out here belongs to the
/// decoder inside, so it is only good while this object is alive: draw from it, or copy out of it, and
/// dispose this when finished.
/// </summary>
/// <remarks>
/// The decoded pixels are kept where the decoder put them rather than copied out. A picture from a
/// camera runs to tens of megabytes, and a second copy of it buys nothing.
/// </remarks>
internal sealed class DecodedPicture : IDisposable
{
    private readonly IDisposable _pixels;
    private bool _disposed;

    internal DecodedPicture(IDisposable pixels, ImageForm form, int width, int height, int frameCount)
    {
        _pixels = pixels;
        Form = form;
        Width = width;
        Height = height;
        FrameCount = frameCount < 1 ? 1 : frameCount;
    }

    /// <summary>The form the file was in.</summary>
    public ImageForm Form { get; }

    /// <summary>The picture width in pixels.</summary>
    public int Width { get; }

    /// <summary>The picture height in pixels.</summary>
    public int Height { get; }

    /// <summary>How many frames the picture holds. One for everything but an animated file.</summary>
    public int FrameCount { get; }

    /// <summary>Whether the picture holds more than one frame.</summary>
    public bool IsAnimated => FrameCount > 1;

    /// <summary>The pixels of frame <paramref name="index"/>. Valid until this picture is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The picture has been disposed.</exception>
    public Surface Frame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pixels switch
        {
            GifImage gif => gif.Frames[Math.Clamp(index, 0, gif.Frames.Count - 1)].AsSurface(),
            PngImage png => png.AsSurface(),
            JpegImage jpeg => jpeg.AsSurface(),
            BmpImage bmp => bmp.AsSurface(),
            TgaImage tga => tga.AsSurface(),
            _ => throw new InvalidOperationException("The picture holds no pixels."),
        };
    }

    /// <summary>How long frame <paramref name="index"/> is shown, in milliseconds. Zero when it is a still.</summary>
    public int Delay(int index)
        => _pixels is GifImage gif ? gif.Frames[Math.Clamp(index, 0, gif.Frames.Count - 1)].DelayMilliseconds : 0;

    /// <summary>How long one pass through every frame takes, in seconds. Zero when it is a still.</summary>
    public double TotalSeconds
    {
        get
        {
            if (_pixels is not GifImage gif)
                return 0;
            long total = 0;
            foreach (GifFrame frame in gif.Frames)
                total += frame.DelayMilliseconds;
            return total / 1000.0;
        }
    }

    /// <summary>Releases the decoded pixels. Every surface handed out becomes invalid.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pixels.Dispose();
    }
}

/// <summary>
/// Reading a picture file into pixels, and writing pixels back out in another form.
///
/// A file is placed by its name first and by its leading bytes when the name says nothing or says
/// something the bytes contradict, so a JPEG saved as <c>.png</c> still opens.
/// </summary>
internal static class ImageConvert
{
    // How many names are tried before giving up on finding one that is free.
    private const int CopyLimit = 999;

    /// <summary>The name ending a GIF file is given.</summary>
    public const string GifExtension = ".gif";

    // The picture codecs are system modules that have to be loaded before they answer. They are loaded
    // on first use and left loaded: pictures are opened one after another, and unloading between them
    // would cost more than it saves.
    private static SystemModule? _pngDecoder;
    private static SystemModule? _pngEncoder;
    private static SystemModule? _jpegDecoder;
    private static SystemModule? _jpegEncoder;

    /// <summary>The forms a picture can be written in, as a sentence fragment.</summary>
    public static string WritableForms => "PNG, JPEG, BMP, TGA or GIF";

    /// <summary>What <paramref name="form"/> is called.</summary>
    public static string Name(ImageForm form) => form switch
    {
        ImageForm.Png => "PNG",
        ImageForm.Jpeg => "JPEG",
        ImageForm.Bmp => "BMP",
        ImageForm.Tga => "TGA",
        ImageForm.Gif => "GIF",
        _ => "unknown",
    };

    /// <summary>What <paramref name="format"/> is called.</summary>
    public static string Name(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Tga => "TGA",
        _ => "PNG",
    };

    /// <summary>The name ending a file written in <paramref name="format"/> is given.</summary>
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tga => ".tga",
        _ => ".png",
    };

    /// <summary>Whether a picture read as <paramref name="form"/> can also be written in that form.</summary>
    public static bool CanWrite(ImageForm form)
        => form is ImageForm.Png or ImageForm.Jpeg or ImageForm.Bmp or ImageForm.Tga or ImageForm.Gif;

    /// <summary>
    /// What can be written from a picture read as <paramref name="form"/> holding
    /// <paramref name="frameCount"/> frames, put plainly enough to show the user.
    /// </summary>
    public static string DescribeWriting(ImageForm form, int frameCount)
    {
        string sentence = $"A copy can be written as {WritableForms}.";
        if (frameCount > 1)
            return $"{sentence} This one holds {frameCount} frames: written as GIF the whole animation is kept, and in any other form the frame on screen is the one that gets written.";
        if (!CanWrite(form))
            return $"{sentence} {Name(form)} itself is read but not written.";
        return sentence;
    }

    /// <summary>The form <paramref name="path"/> names, going by the name alone.</summary>
    public static ImageForm FromName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathUtil.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageForm.Png,
            ".jpg" or ".jpeg" => ImageForm.Jpeg,
            ".bmp" => ImageForm.Bmp,
            ".tga" => ImageForm.Tga,
            ".gif" => ImageForm.Gif,
            _ => ImageForm.Unknown,
        };
    }

    /// <summary>The form <paramref name="data"/> begins like, or unknown when nothing matches.</summary>
    public static ImageForm Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G'
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return ImageForm.Png;
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ImageForm.Jpeg;
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
            return ImageForm.Bmp;
        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
            return ImageForm.Gif;

        // TGA is checked last and on its shape rather than a signature: it carries none at the front,
        // so anything decided here would otherwise be taken from a form that does.
        return LooksLikeTga(data) ? ImageForm.Tga : ImageForm.Unknown;
    }

    /// <summary>
    /// The form <paramref name="path"/> holds. The name decides it, and the leading bytes of
    /// <paramref name="data"/> overrule the name whenever they say something definite, so a file saved
    /// under the wrong ending still opens as what it is.
    /// </summary>
    public static ImageForm FormOf(string path, ReadOnlySpan<byte> data)
    {
        ImageForm sniffed = Sniff(data);
        return sniffed == ImageForm.Unknown ? FromName(path) : sniffed;
    }

    /// <summary>Reads and decodes the picture at <paramref name="path"/>.</summary>
    /// <exception cref="NotSupportedException">The file is not in a form that is read.</exception>
    public static DecodedPicture Decode(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = FileSystem.ReadAllBytes(path);
        ImageForm form = FormOf(path, data);
        switch (form)
        {
            case ImageForm.Png:
            {
                Load(ref _pngDecoder, SystemModuleId.PngDec);
                PngImage png = PngImage.Decode(data);
                return new DecodedPicture(png, form, png.Width, png.Height, 1);
            }

            case ImageForm.Jpeg:
            {
                Load(ref _jpegDecoder, SystemModuleId.JpegDec);
                JpegImage jpeg = JpegImage.Decode(data);
                return new DecodedPicture(jpeg, form, jpeg.Width, jpeg.Height, 1);
            }

            case ImageForm.Bmp:
            {
                BmpImage bmp = BmpImage.Decode(data);
                return new DecodedPicture(bmp, form, bmp.Width, bmp.Height, 1);
            }

            case ImageForm.Tga:
            {
                TgaImage tga = TgaImage.Decode(data);
                return new DecodedPicture(tga, form, tga.Width, tga.Height, 1);
            }

            case ImageForm.Gif:
            {
                GifImage gif = GifImage.Decode(data);
                return new DecodedPicture(gif, form, gif.Width, gif.Height, gif.Frames.Count);
            }

            default:
                throw new NotSupportedException(
                    $"{PathUtil.GetFileName(path)} is not a picture this reads. It reads PNG, JPEG, BMP, TGA and GIF.");
        }
    }

    /// <summary>
    /// Writes <paramref name="picture"/> to <paramref name="path"/> in <paramref name="format"/>, at the
    /// quality and compression the settings ask for. <paramref name="sampling"/> chooses how a JPEG stores
    /// colour and is ignored by the other forms.
    /// </summary>
    public static void Save(Surface picture, string path, ImageFormat format, AppSettings settings, JpegSampling sampling = JpegSampling.Ycc420)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(settings);
        switch (format)
        {
            case ImageFormat.Jpeg:
                Load(ref _jpegEncoder, SystemModuleId.JpegEnc);
                JpegEncoder.Save(picture, path, settings.JpegQuality, sampling);
                break;

            case ImageFormat.Bmp:
                BmpEncoder.Save(picture, path);
                break;

            case ImageFormat.Tga:
                TgaEncoder.Save(picture, path, includeAlpha: true);
                break;

            default:
                Load(ref _pngEncoder, SystemModuleId.PngEnc);
                PngEncoder.Save(picture, path, settings.PngCompression);
                break;
        }
    }

    /// <summary>
    /// Writes <paramref name="picture"/> to <paramref name="path"/> as a single-frame GIF. Fully
    /// transparent pixels are kept transparent; every other pixel is opaque. The encoder needs no module.
    /// </summary>
    public static void SaveGif(Surface picture, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        GifEncoder.Save(picture, path);
    }

    /// <summary>
    /// Writes <paramref name="picture"/> to <paramref name="path"/> as GIF. A picture holding more than one
    /// frame is written as an animation that keeps every frame with its own delay and repeats without end;
    /// a still picture is written as a single frame. Each frame is already composed, so it is written whole
    /// and cleared to transparent before the next, which reproduces what was on screen frame for frame.
    /// </summary>
    public static void WriteGif(DecodedPicture picture, string path)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (picture.FrameCount > 1)
        {
            var frames = new GifFrameSource[picture.FrameCount];
            for (int index = 0; index < picture.FrameCount; index++)
                frames[index] = new GifFrameSource(picture.Frame(index), picture.Delay(index), GifDisposal.RestoreBackground);
            GifEncoder.SaveAnimation(frames, path, loopCount: 0);
        }
        else
        {
            GifEncoder.Save(picture.Frame(0), path);
        }
    }

    /// <summary>Where converting <paramref name="sourcePath"/> to <paramref name="format"/> writes.</summary>
    public static string TargetPath(string sourcePath, ImageFormat format)
        => PathUtil.ChangeExtension(sourcePath, Extension(format));

    /// <summary>
    /// A path beside <paramref name="sourcePath"/>, in <paramref name="format"/>, that no file is using
    /// yet, so writing a copy never stands on something already there.
    /// </summary>
    /// <exception cref="InvalidOperationException">Every name that was tried is taken.</exception>
    public static string CopyPath(string sourcePath, ImageFormat format)
        => CopyPath(sourcePath, Extension(format));

    /// <summary>
    /// A path beside <paramref name="sourcePath"/>, ending in <paramref name="extension"/>, that no file
    /// is using yet, so writing a copy never stands on something already there.
    /// </summary>
    /// <exception cref="InvalidOperationException">Every name that was tried is taken.</exception>
    public static string CopyPath(string sourcePath, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        string folder = PathUtil.GetDirectoryName(sourcePath);
        string stem = PathUtil.GetFileNameWithoutExtension(sourcePath);
        for (int index = 1; index <= CopyLimit; index++)
        {
            string name = index == 1 ? $"{stem}-copy{extension}" : $"{stem}-copy-{index}{extension}";
            string candidate = PathUtil.Combine(folder, name);
            if (!FileSystem.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException($"{stem} already has {CopyLimit} copies beside it.");
    }

    // TGA carries no signature at the front. A footer was added to the format later; when it is absent
    // the header fields are checked against the shapes that actually decode, which is narrow enough that
    // an ordinary file does not pass by accident.
    private static bool LooksLikeTga(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 26 && data[^18..].StartsWith("TRUEVISION-XFILE"u8))
            return true;
        if (data.Length < 18)
            return false;

        int colorMapType = data[1];
        int imageType = data[2];
        int width = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int depth = data[16];
        return colorMapType == 0
            && (imageType == 2 || imageType == 10)
            && (depth == 24 || depth == 32)
            && width > 0
            && height > 0;
    }

    private static void Load(ref SystemModule? slot, SystemModuleId id) => slot ??= SystemModule.Load(id);
}
