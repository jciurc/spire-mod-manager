// Read Godot 4.5 resource packs (format v3), e.g. the game's SlayTheSpire2.pck, to look at its scenes and art.
//
//   dotnet run tools/pck.cs list <pack> [filter]           paths containing filter (case-insensitive)
//   dotnet run tools/pck.cs extract <pack> <path> <out>    one file; for an imported texture
//                                                          (*.png), saves the image inside its .ctex
// Writing packs lives in build.cs.
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 2 || args[0] is not ("list" or "extract") || (args[0] == "extract" && args.Length < 4))
{
    Console.Error.WriteLine("usage: dotnet run tools/pck.cs list <pack> [filter] | extract <pack> <path> <out>");
    return 1;
}
var pack = args[1];
var entries = Directory(pack);

if (args[0] == "list")
{
    var filter = args.ElementAtOrDefault(2) ?? "";
    foreach (var (path, _, size) in entries.Where(e => e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"{path}\t{size}");
    return 0;
}

var (target, outPath) = (args[2].Replace("res://", ""), args[3]);
if (target.EndsWith(".png") && entries.Any(e => e.Path == target + ".import"))
{
    // Imported textures live in .godot/imported/*.ctex: a 56-byte header, then a WebP or PNG.
    var import = Encoding.UTF8.GetString(Read(pack, entries, target + ".import"));
    var ctex = Regex.Match(import, "path[^=]*=\"res://([^\"]+\\.ctex)\"").Groups[1].Value;
    var data = Read(pack, entries, ctex);
    var start = data.AsSpan().IndexOf("RIFF"u8) is var webp and >= 0 ? webp : data.AsSpan().IndexOf("\x89PNG"u8);
    if (start < 0)
    {
        Console.Error.WriteLine($"{ctex}: no WebP/PNG inside (GPU-compressed texture)");
        return 1;
    }
    File.WriteAllBytes(outPath, data[start..]);
    Console.WriteLine($"{ctex} -> {outPath} ({(data[start] == 'R' ? "webp" : "png")})");
    return 0;
}
File.WriteAllBytes(outPath, Read(pack, entries, target));
Console.WriteLine($"{target} -> {outPath}");
return 0;

static List<(string Path, long Offset, long Size)> Directory(string pack)
{
    using var r = new BinaryReader(File.OpenRead(pack));
    if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "GDPC" || r.ReadInt32() != 3)
        throw new InvalidDataException($"{pack}: not a v3 Godot pack");
    r.ReadBytes(12); // engine version
    var flags = r.ReadInt32();
    if ((flags & 1) != 0)
        throw new InvalidDataException("encrypted directory not supported");
    var fileBase = r.ReadInt64();
    r.BaseStream.Position = r.ReadInt64();
    var count = r.ReadInt32();
    var list = new List<(string, long, long)>(count);
    for (var i = 0; i < count; i++)
    {
        var name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())).TrimEnd('\0');
        var offset = r.ReadInt64();
        var size = r.ReadInt64();
        r.ReadBytes(16 + 4); // MD5, flags
        list.Add((name, offset + fileBase, size));
    }
    return list;
}

static byte[] Read(string pack, List<(string Path, long Offset, long Size)> entries, string path)
{
    var (_, offset, size) = entries.FirstOrDefault(e => e.Path == path);
    if (size == 0 && !entries.Any(e => e.Path == path))
        throw new FileNotFoundException(path);
    using var f = File.OpenRead(pack);
    f.Position = offset;
    var buffer = new byte[size];
    f.ReadExactly(buffer);
    return buffer;
}
