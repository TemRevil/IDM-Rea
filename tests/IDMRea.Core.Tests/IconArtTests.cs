using System.Buffers.Binary;
using System.IO.Compression;
using IDMRea.Contracts;
using Xunit;
namespace IDMRea.Core.Tests;

public class IconArtTests
{
    private static readonly Rgb Blue = new(0x00, 0x78, 0xD4);

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] rgba, int size, int x, int y)
    {
        var i = (y * size + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    [Fact]
    public void PaintsRingInTheGivenColourAroundTheMarkOnTheTile()
    {
        var rgba = IconArt.Rgba(96, Blue);
        Assert.Equal(96 * 96 * 4, rgba.Length);
        Assert.Equal((Blue.R, Blue.G, Blue.B, (byte)255), Pixel(rgba, 96, 48, 24));          // top of the ring
        Assert.Equal((IconArt.Mark.R, IconArt.Mark.G, IconArt.Mark.B, (byte)255), Pixel(rgba, 96, 47, 51)); // chevron stroke
        Assert.Equal((IconArt.Tile.R, IconArt.Tile.G, IconArt.Tile.B, (byte)255), Pixel(rgba, 96, 48, 36)); // inside the ring
        Assert.Equal((byte)0, Pixel(rgba, 96, 0, 0).A);                                        // rounded corner
    }

    [Fact]
    public void PngHasValidSignatureHeaderPixelsAndChecksums()
    {
        var png = IconArt.Png(32, IconArt.Rgba(32, Blue));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(32, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(32, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, png[^4..]);                     // CRC of an empty IEND
        Assert.Equal(Crc(png, 12, 17), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(29)));  // IHDR type + data
        var idatLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using var inflate = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        var raw = new MemoryStream(); inflate.CopyTo(raw);
        Assert.Equal(32 * (1 + 32 * 4), raw.Length);
    }

    /// <summary>An independent bitwise CRC-32, to check the one IconArt writes.</summary>
    private static uint Crc(byte[] data, int start, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (var i = start; i < start + length; i++)
        {
            crc ^= data[i];
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    [Fact]
    public void IcoListsEverySizeWithPngImagesAtTheirOffsets()
    {
        var ico = IconArt.Ico(Blue);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.Equal(IconArt.IcoSizes.Length, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        for (var n = 0; n < IconArt.IcoSizes.Length; n++)
        {
            var entry = 6 + 16 * n;
            var size = IconArt.IcoSizes[n];
            Assert.Equal(size >= 256 ? 0 : size, ico[entry]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 8));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 12));
            Assert.True(offset + length <= ico.Length);
            Assert.Equal(0x89, ico[offset]);
            Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(ico.AsSpan(offset + 16)));
        }
    }

    [Fact]
    public void KeepsTheAccentWhenItStandsOutOnTheTile() => Assert.Equal(Blue, IconArt.RingFor(Blue, new Rgb(0x99, 0xEB, 0xFF)));

    [Fact]
    public void UsesTheLighterShadeWhenTheAccentIsTooDarkForTheTile()
    {
        var lighter = new Rgb(0x9A, 0x9A, 0x9A);
        Assert.Equal(lighter, IconArt.RingFor(new Rgb(0x2D, 0x2D, 0x2D), lighter));
    }

    [Fact]
    public void FilesAreWrittenOncePerColour()
    {
        var folder = Path.Combine(Path.GetTempPath(), "idmrea-icons-" + Guid.NewGuid());
        try
        {
            var (ico, png) = IconArt.Files(Blue, folder);
            Assert.Equal(Path.Combine(folder, "ring-0078D4.ico"), ico);
            Assert.Equal(Path.Combine(folder, "ring-0078D4.png"), png);
            Assert.Equal(IconArt.Ico(Blue), File.ReadAllBytes(ico));
            var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(ico, old); File.SetLastWriteTimeUtc(png, old);
            Assert.Equal((ico, png), IconArt.Files(Blue, folder));
            Assert.Equal(old, File.GetLastWriteTimeUtc(ico));
            Assert.Equal(old, File.GetLastWriteTimeUtc(png));
            Assert.EndsWith("ring-A8A29E.ico", IconArt.Files(IconArt.ExeRing, folder).Ico);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
