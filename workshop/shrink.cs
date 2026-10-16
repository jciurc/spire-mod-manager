#:property TargetFramework=net10.0-windows
#:package System.Drawing.Common@10.0.0
// Shrinks Workshop screenshots: each becomes a JPG at most 1920 px wide, at the best quality under Steam's 1 MB,
// replacing the original. image.png stays a PNG (SHOTS.md). Windows only (System.Drawing).
// Usage: dotnet run workshop/shrink.cs [picture ...]     (no pictures given: everything in workshop/previews/)
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

const int MaxWidth = 1920;
const long MaxBytes = 1_000_000; // the same safe reading of 1 MB as prepare.cs

var previews = Path.Combine(ScriptDir(), "previews");
if (args.Length == 0 && !Directory.Exists(previews))
{
    Console.Error.WriteLine("workshop/previews/ doesn't exist yet: put the screenshots there first");
    return 1;
}
var pictures = args.Length > 0 ? args.Select(Path.GetFullPath).ToArray() : Directory.GetFiles(previews);
foreach (var path in pictures)
{
    var extension = Path.GetExtension(path).ToLowerInvariant();
    if (Path.GetFileName(path) == "image.png" || extension is not (".jpg" or ".jpeg" or ".png"))
        continue;
    var (jpg, width, height) = Shrink(path, keepIfFine: extension is ".jpg" or ".jpeg");
    if (jpg == null)
    {
        Console.WriteLine($"{Path.GetFileName(path)}: fine as it is");
        continue;
    }
    if (jpg.Length >= MaxBytes)
    {
        Console.Error.WriteLine($"{Path.GetFileName(path)}: still over 1 MB at the lowest quality; crop it");
        continue;
    }
    var target = Path.ChangeExtension(path, ".jpg");
    File.WriteAllBytes(target, jpg);
    if (target != path)
        File.Delete(path);
    Console.WriteLine($"{Path.GetFileName(target)}: {width}x{height}, {jpg.Length:N0} bytes");
}
return 0;

// The picture as a JPG, narrowed to MaxWidth, at the highest quality under MaxBytes; null for a JPG that already fits
// (keepIfFine), so running again doesn't wear it down. Read into memory first, so the file isn't locked.
static (byte[]? Jpg, int Width, int Height) Shrink(string path, bool keepIfFine)
{
    using var original = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
    if (keepIfFine && original.Width <= MaxWidth && new FileInfo(path).Length < MaxBytes)
        return (null, original.Width, original.Height);

    var (width, height) = original.Width > MaxWidth
        ? (MaxWidth, (int)Math.Round((double)original.Height * MaxWidth / original.Width))
        : (original.Width, original.Height);
    using var picture = new Bitmap(width, height);
    using (var graphics = Graphics.FromImage(picture))
    using (var edges = new ImageAttributes())
    {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        edges.SetWrapMode(WrapMode.TileFlipXY); // no faint border from sampling past the edge
        graphics.DrawImage(original, new Rectangle(0, 0, width, height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel, edges);
    }

    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    byte[] jpg = [];
    for (var quality = 92L; quality >= 50 && (jpg.Length == 0 || jpg.Length >= MaxBytes); quality -= 6)
    {
        using var buffer = new MemoryStream();
        using var settings = new EncoderParameters(1);
        settings.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        picture.Save(buffer, codec, settings);
        jpg = buffer.ToArray();
    }
    return (jpg, width, height);
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";
