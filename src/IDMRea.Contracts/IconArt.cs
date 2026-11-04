using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace IDMRea.Contracts;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The IDM-Rea mark: a progress ring in three parts around a small chevron, on a charcoal tile.
/// The ring takes the Windows accent colour, so the icon is drawn here at run time rather than shipped fixed.
/// </summary>
public static class IconArt
{
    public static readonly Rgb Tile = new(0x1F, 0x1D, 0x1B), Mark = new(0xF4, 0xEF, 0xEA);
    /// <summary>The ring in the exe's own icon file, which Windows shows before the app runs.</summary>
    public static readonly Rgb DefaultRing = new(0xC4, 0x65, 0x32);
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private const int Samples = 4; // per axis, for anti-aliasing
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly record struct Shape(double Radius, double Ring, double Gap, (double X, double Y)[] Chevron, double Stroke);

    /// <summary>Geometry in a 96-unit square. Small sizes get heavier strokes so the mark stays readable.</summary>
    private static Shape ShapeFor(int size) => size switch
    {
        <= 24 => new(25.0, 10.0, 11.0, [(37, 43), (48, 54), (59, 43)], 8.0),
        <= 48 => new(24.5, 8.0, 10.0, [(39, 44), (48, 53), (57, 44)], 6.0),
        _ => new(24.0, 7.0, 9.27, [(40, 44), (48, 52), (56, 44)], 5.0),
    };

    /// <summary>The accent, or its lighter shade when the accent would sink into the dark tile.</summary>
    public static Rgb RingFor(Rgb accent, Rgb lighter) => Contrast(accent, Tile) >= 2.5 ? accent : lighter;

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double L(Rgb c)
        {
            static double Channel(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static bool InTile(double x, double y)
    {
        const double corner = 22.0, half = 48.0;
        var qx = Math.Abs(x - 48) - (half - corner);
        var qy = Math.Abs(y - 48) - (half - corner);
        return Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - corner <= 0;
    }

    private static bool InRing(double x, double y, Shape g)
    {
        double dx = x - 48, dy = y - 48;
        if (Math.Abs(Math.Sqrt(dx * dx + dy * dy) - g.Radius) > g.Ring / 2) return false;
        // Clockwise on screen from 12 o'clock, three equal dashes.
        var turn = (Math.Atan2(dy, dx) + Math.PI / 2 + 2 * Math.PI) % (2 * Math.PI);
        var period = 2 * Math.PI * g.Radius / 3;
        return turn * g.Radius % period < period - g.Gap;
    }

    private static double SegmentDistance(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        var t = Math.Clamp(((x - a.X) * vx + (y - a.Y) * vy) / (vx * vx + vy * vy), 0, 1);
        return Math.Sqrt(Math.Pow(x - (a.X + t * vx), 2) + Math.Pow(y - (a.Y + t * vy), 2));
    }

    private static bool InChevron(double x, double y, Shape g) =>
        Math.Min(SegmentDistance(x, y, g.Chevron[0], g.Chevron[1]), SegmentDistance(x, y, g.Chevron[1], g.Chevron[2])) <= g.Stroke / 2;

    /// <summary>Straight (not premultiplied) RGBA, row by row from the top.</summary>
    public static byte[] Rgba(int size, Rgb ring)
    {
        var g = ShapeFor(size);
        var scale = 96.0 / size;
        var pixels = new byte[size * size * 4];
        for (var py = 0; py < size; py++)
            for (var px = 0; px < size; px++)
            {
                double r = 0, gr = 0, b = 0, a = 0;
                for (var sy = 0; sy < Samples; sy++)
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var x = (px + (sx + 0.5) / Samples) * scale;
                        var y = (py + (sy + 0.5) / Samples) * scale;
                        if (!InTile(x, y)) continue;
                        var c = InChevron(x, y, g) ? Mark : InRing(x, y, g) ? ring : Tile;
                        r += c.R; gr += c.G; b += c.B; a++;
                    }
                if (a == 0) continue;
                var i = (py * size + px) * 4;
                pixels[i] = (byte)Math.Round(r / a); pixels[i + 1] = (byte)Math.Round(gr / a); pixels[i + 2] = (byte)Math.Round(b / a);
                pixels[i + 3] = (byte)Math.Round(255 * a / (Samples * Samples));
            }
        return pixels;
    }

    public static byte[] Png(int size, byte[] rgba)
    {
        var raw = new byte[size * (1 + size * 4)];
        for (var y = 0; y < size; y++) Buffer.BlockCopy(rgba, y * size * 4, raw, y * (1 + size * 4) + 1, size * 4);
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; header[9] = 6; // 8 bits per channel, RGBA
        var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] data)
    {
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(kind, body);
        data.CopyTo(body, 4);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        output.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(body)); output.Write(number);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var d in data) crc = CrcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>An .ico holding a PNG image for each of <see cref="IcoSizes"/>.</summary>
    public static byte[] Ico(Rgb ring)
    {
        var images = IcoSizes.Select(size => Png(size, Rgba(size, ring))).ToArray();
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Length);
        var offset = 6 + 16 * images.Length;
        for (var n = 0; n < images.Length; n++)
        {
            var side = (byte)(IcoSizes[n] >= 256 ? 0 : IcoSizes[n]);
            writer.Write(side); writer.Write(side); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[n].Length); writer.Write(offset);
            offset += images[n].Length;
        }
        foreach (var image in images) writer.Write(image);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// The .ico and 256 px .png for this ring colour, drawn once and kept under the data folder.
    /// The app and the engine may both ask at once, so each file is written beside and then moved into place.
    /// </summary>
    public static (string Ico, string Png) Files(Rgb ring, string? folder = null)
    {
        folder ??= Path.Combine(Protocol.DataDirectory, "icons");
        var ico = Path.Combine(folder, $"ring-{ring.Hex}.ico");
        var png = Path.Combine(folder, $"ring-{ring.Hex}.png");
        Directory.CreateDirectory(folder);
        if (!File.Exists(ico)) Place(ico, Ico(ring));
        if (!File.Exists(png)) Place(png, Png(256, Rgba(256, ring)));
        return (ico, png);
    }

    /// <summary>Never replaces a file already there: another process may have just written the same one.</summary>
    private static void Place(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, path, overwrite: false); }
        catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
    }
}
