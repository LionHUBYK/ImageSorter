using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageSorter;

/// <param name="Bitmap">Frozen, safe to hand to the UI thread.</param>
/// <param name="FullWidth">Pixel size of the original, after rotation.</param>
/// <param name="IsFull">False when the bitmap was decoded smaller than the original to save time.</param>
/// <param name="GifData">The raw file, present only for animated GIFs so the viewer can play them.</param>
public sealed record DecodedImage(BitmapSource Bitmap, int FullWidth, int FullHeight, bool IsFull, byte[]? GifData = null);

/// <summary>
/// Turns files into bitmaps. Everything here is safe to call from background threads.
/// Common formats go through Windows' own decoders (fast); HEIC and RAW fall back to ImageMagick
/// so they work even when the Windows codec packs aren't installed.
/// </summary>
public static class ImageDecoder
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".webp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> HeifExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".hif", ".avif",
    };

    private static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".raf", ".orf", ".rw2",
        ".pef", ".srw", ".raw", ".rwl", ".3fr", ".erf", ".kdc", ".dcr", ".mrw", ".x3f", ".iiq",
    };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts",
    };

    private static readonly string[] OrientationQueries =
    [
        "/app1/ifd/{ushort=274}",          // JPEG EXIF
        "/ifd/{ushort=274}",               // TIFF
        "/app1/{ushort=0}/{ushort=274}",
    ];

    /// <summary>Null means "not something this app sorts".</summary>
    public static MediaKind? Classify(string path)
    {
        string ext = Path.GetExtension(path);
        if (ImageExt.Contains(ext)) return MediaKind.Image;
        if (HeifExt.Contains(ext)) return MediaKind.Heif;
        if (RawExt.Contains(ext)) return MediaKind.Raw;
        if (VideoExt.Contains(ext)) return MediaKind.Video;
        return null;
    }

    /// <summary>Decode a still image. <paramref name="maxSide"/> caps the longer edge; 0 means full size.</summary>
    public static DecodedImage? Decode(string path, int maxSide)
    {
        MediaKind kind = Classify(path) ?? MediaKind.Image;
        if (kind == MediaKind.Video) return null;

        byte[] data = ReadAll(path);

        if (kind == MediaKind.Raw)
        {
            // Nearly every RAW file carries a camera-rendered JPEG. Showing that is instant;
            // developing the sensor data takes seconds per photo.
            return DecodeRawPreview(data, maxSide) ?? DecodeMagick(path, maxSide);
        }

        if (kind == MediaKind.Heif)
        {
            // The Windows HEIF codec already applies the rotation stored in the file.
            try { return DecodeWic(data, 0, maxSide, applyExif: false, fallbackOrientation: 1, gif: false); }
            catch { return DecodeMagick(path, maxSide); }
        }

        bool gif = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        try { return DecodeWic(data, 0, maxSide, applyExif: true, fallbackOrientation: 1, gif); }
        catch { return DecodeMagick(path, maxSide); }
    }

    /// <summary>A small picture for the filmstrip and the sorter cards.</summary>
    public static BitmapSource? Thumbnail(string path, MediaKind kind, int side)
    {
        if (kind == MediaKind.Video) return ShellThumbnail(path, side);

        if (kind is MediaKind.Raw or MediaKind.Heif)
        {
            // Explorer's thumbnail cache is the cheapest source when the codec is installed.
            BitmapSource? shell = ShellThumbnail(path, side);
            if (shell is not null) return shell;
        }

        try { return Decode(path, side)?.Bitmap; }
        catch { return kind == MediaKind.Image ? ShellThumbnail(path, side) : null; }
    }

    /// <summary>Shrink an already-decoded bitmap (used when a thumbnail hasn't loaded yet).</summary>
    public static BitmapSource Downscale(BitmapSource source, int side)
    {
        double scale = (double)side / Math.Max(source.PixelWidth, source.PixelHeight);
        if (scale >= 1) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var baked = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        baked.Freeze();
        return baked;
    }

    private static byte[] ReadAll(string path)
    {
        // Share generously: the user may move or rename the file while a thumbnail is still loading.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        if (stream.Length > 1_500_000_000) throw new IOException("File is too large to preview.");
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    // ---- Windows Imaging Component ----

    private static DecodedImage DecodeWic(byte[] data, int offset, int maxSide, bool applyExif, int fallbackOrientation, bool gif)
    {
        int count = data.Length - offset;
        int width, height, frames, orientation = 1;

        using (var probe = new MemoryStream(data, offset, count, writable: false))
        {
            var decoder = BitmapDecoder.Create(probe,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            frames = decoder.Frames.Count;
            if (applyExif) orientation = ReadOrientation(frame);
        }
        if (orientation == 1) orientation = fallbackOrientation;

        bool scaled = maxSide > 0 && Math.Max(width, height) > maxSide;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(data, offset, count, writable: false);
        if (scaled)
        {
            // Asking the decoder for a small size lets JPEG skip most of the work.
            if (width >= height) image.DecodePixelWidth = maxSide;
            else image.DecodePixelHeight = maxSide;
        }
        image.EndInit();

        BitmapSource bitmap = Orient(image, orientation);
        bitmap.Freeze();
        if (orientation >= 5) (width, height) = (height, width);
        return new DecodedImage(bitmap, width, height, !scaled, gif && frames > 1 ? data : null);
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata) return 1;
            foreach (string query in OrientationQueries)
            {
                try
                {
                    if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8)
                        return value;
                }
                catch
                {
                    // This query doesn't apply to this format; try the next one.
                }
            }
        }
        catch
        {
            // No readable metadata: treat as upright.
        }
        return 1;
    }

    /// <summary>Apply an EXIF orientation (1-8) so phone photos aren't shown sideways.</summary>
    private static BitmapSource Orient(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
            8 => new RotateTransform(270),
            _ => null,
        };
        if (transform is null) return source;
        var rotated = new TransformedBitmap(source, transform);
        return new CachedBitmap(rotated, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    // ---- RAW: find the embedded JPEG preview ----

    private static DecodedImage? DecodeRawPreview(byte[] data, int maxSide)
    {
        var candidates = new List<(int Offset, int Width, int Height)>();
        ReadOnlySpan<byte> startOfImage = [0xFF, 0xD8, 0xFF];
        int position = 0;
        while (position < data.Length - 4 && candidates.Count < 64)
        {
            int found = data.AsSpan(position).IndexOf(startOfImage);
            if (found < 0) break;
            int offset = position + found;
            position = offset + 2;
            if (TryReadJpegSize(data, offset, out int w, out int h)) candidates.Add((offset, w, h));
        }

        int containerOrientation = ReadTiffOrientation(data);
        foreach (var c in candidates.OrderByDescending(c => (long)c.Width * c.Height))
        {
            if (Math.Max(c.Width, c.Height) < 640) break;   // only tiny thumbnails left
            try
            {
                return DecodeWic(data, c.Offset, maxSide, applyExif: true, containerOrientation, gif: false);
            }
            catch
            {
                // Not a JPEG Windows can read after all; try the next biggest.
            }
        }
        return null;
    }

    /// <summary>Walk JPEG segments from a start-of-image marker to the frame header.</summary>
    private static bool TryReadJpegSize(byte[] d, int offset, out int width, out int height)
    {
        width = height = 0;
        int p = offset + 2;
        for (int guard = 0; guard < 200 && p + 4 <= d.Length; guard++)
        {
            if (d[p] != 0xFF) return false;
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) return false;

            int length = (d[p + 2] << 8) | d[p + 3];
            if (length < 2) return false;

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (p + 9 > d.Length) return false;
                height = (d[p + 5] << 8) | d[p + 6];
                width = (d[p + 7] << 8) | d[p + 8];
                return width > 0 && height > 0;
            }
            // Other frame types (lossless, arithmetic) are the sensor data itself, not a viewable preview.
            if (marker is >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return false;

            p += 2 + length;
        }
        return false;
    }

    /// <summary>Most RAW formats are TIFF containers; the orientation tag lives in the first directory.</summary>
    private static int ReadTiffOrientation(byte[] d)
    {
        if (d.Length < 16) return 1;
        bool little = d[0] == (byte)'I' && d[1] == (byte)'I';
        bool big = d[0] == (byte)'M' && d[1] == (byte)'M';
        if (!little && !big) return 1;

        uint U16(long at) => little ? (uint)(d[at] | (d[at + 1] << 8)) : (uint)((d[at] << 8) | d[at + 1]);
        uint U32(long at) => little
            ? (uint)(d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24))
            : (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);

        long directory = U32(4);
        if (directory < 8 || directory + 2 > d.Length) return 1;
        uint entries = U16(directory);
        for (uint i = 0; i < entries; i++)
        {
            long entry = directory + 2 + 12L * i;
            if (entry + 12 > d.Length) break;
            if (U16(entry) != 0x0112) continue;
            uint value = U16(entry + 8);
            return value is >= 1 and <= 8 ? (int)value : 1;
        }
        return 1;
    }

    // ---- ImageMagick fallback ----

    private static DecodedImage DecodeMagick(string path, int maxSide)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        int fullWidth = (int)image.Width, fullHeight = (int)image.Height;

        bool scaled = maxSide > 0 && Math.Max(fullWidth, fullHeight) > maxSide;
        if (scaled) image.Thumbnail((uint)maxSide, (uint)maxSide);

        int width = (int)image.Width, height = (int)image.Height;
        using IPixelCollection<byte> pixels = image.GetPixels();
        byte[] bgra = pixels.ToByteArray(PixelMapping.BGRA)
                      ?? throw new InvalidOperationException("Couldn't read pixels.");
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return new DecodedImage(bitmap, fullWidth, fullHeight, !scaled);
    }

    // ---- Explorer thumbnails (videos, and anything Windows has a codec for) ----

    public static BitmapSource? ShellThumbnail(string path, int side)
    {
        const int SIIGBF_THUMBNAILONLY = 0x8;   // fail rather than hand back a generic file icon
        Guid iid = typeof(Native.IShellItemImageFactory).GUID;
        Native.IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory is null)
                return null;
            if (factory.GetImage(new Native.SIZE { cx = side, cy = side }, SIIGBF_THUMBNAILONLY, out hBitmap) != 0
                || hBitmap == IntPtr.Zero)
                return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            var baked = new CachedBitmap(opaque, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            baked.Freeze();
            return baked;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}
