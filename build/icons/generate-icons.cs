#:package SkiaSharp@2.88.9
#:package SkiaSharp.NativeAssets.Linux@2.88.9
#:property PublishAot=false
#:property ManagePackageVersionsCentrally=false

// Generates the app icons from the vector design in src/Keel.Desktop/Assets/keel-icon.svg (same paths):
// PNGs (16-1024), keel.ico (Windows) and keel.icns (macOS). Run from the repository root:
//   dotnet run build/icons/generate-icons.cs
// The outputs are checked in; rerun only when the design changes.
using System.Buffers.Binary;
using SkiaSharp;

var assets = Path.Combine("src", "Keel.Desktop", "Assets");
var outDir = Path.Combine("build", "icons", "out");
Directory.CreateDirectory(assets);
Directory.CreateDirectory(outDir);

(string Path, string Color)[] shapes =
[
    ("M50 14 L50 50 L74 50 Z", "#FFFFFF"),
    ("M46 22 L46 50 L27 50 Z", "#BFE7EF"),
    ("M17 56 L79 56 L68 69 L28 69 Z", "#FFFFFF"),
    ("M45 69 L51 69 L50 83 L46 83 Z", "#BFE7EF"),
];

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 96f);
    using (var background = new SKPaint { Color = SKColor.Parse("#0B6477"), IsAntialias = true })
    {
        canvas.DrawRoundRect(new SKRect(0, 0, 96, 96), 22, 22, background);
    }

    foreach (var (data, color) in shapes)
    {
        using var path = SKPath.ParseSvgPathData(data);
        using var paint = new SKPaint { Color = SKColor.Parse(color), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, paint);
    }

    using var image = surface.Snapshot();
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    return png.ToArray();
}

var pngs = new Dictionary<int, byte[]>();
foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 })
{
    pngs[size] = Render(size);
    File.WriteAllBytes(Path.Combine(outDir, $"keel-{size}.png"), pngs[size]);
}

// App assets: window icon and About/first-run logo.
File.WriteAllBytes(Path.Combine(assets, "keel-icon-64.png"), pngs[64]);
File.WriteAllBytes(Path.Combine(assets, "keel-icon-256.png"), pngs[256]);
File.WriteAllBytes(Path.Combine(outDir, "keel.png"), pngs[512]); // Linux (AppImage/.deb) icon

// Windows .ico: PNG-compressed entries (Vista+).
int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
using (var ico = new MemoryStream())
using (var w = new BinaryWriter(ico))
{
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)icoSizes.Length);
    var offset = 6 + (16 * icoSizes.Length);
    foreach (var size in icoSizes)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(pngs[size].Length);
        w.Write(offset);
        offset += pngs[size].Length;
    }

    foreach (var size in icoSizes)
    {
        w.Write(pngs[size]);
    }

    w.Flush();
    File.WriteAllBytes(Path.Combine(assets, "keel.ico"), ico.ToArray());
}

// macOS .icns: PNG entries (icp4 16, icp5 32, icp6 64, ic07 128, ic08 256, ic09 512, ic10 1024).
(string Type, int Size)[] icnsEntries = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];
using (var icns = new MemoryStream())
{
    var header = new byte[8];
    icns.Write(header);
    foreach (var (type, size) in icnsEntries)
    {
        var entry = new byte[8];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(entry, 0);
        BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(4), 8 + pngs[size].Length);
        icns.Write(entry);
        icns.Write(pngs[size]);
    }

    var bytes = icns.ToArray();
    System.Text.Encoding.ASCII.GetBytes("icns").CopyTo(bytes, 0);
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), bytes.Length);
    File.WriteAllBytes(Path.Combine(assets, "keel.icns"), bytes);
}

Console.WriteLine($"Icons written to {assets} and {outDir}");
