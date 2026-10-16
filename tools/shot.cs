// Screenshots for a pull request: each picture is shrunk to a JPG (workshop/shrink.cs), committed to the
// pr-screenshots branch in a folder named after the current branch, and printed as Markdown to paste. Fixture runs
// only: a --real shot (real-*.png) shows your own mods and data, so it's refused.
//
// Usage: dotnet run tools/shot.cs <picture> [picture ...] [--dir <folder>]
//   e.g. dotnet run tools/shot.cs out/selftest/16-long-details.png
// --dir names the folder when the branch name won't do (a detached HEAD). The same name again replaces the
// picture everywhere it's shown.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

const string Branch = "pr-screenshots";

var root = Path.GetFullPath(Path.Combine(ScriptDir(), ".."));
var pictures = new List<string>();
string? folder = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--dir" && i + 1 < args.Length)
        folder = args[++i];
    else
        pictures.Add(Path.GetFullPath(args[i]));
}
if (pictures.Count == 0)
{
    Console.Error.WriteLine("usage: dotnet run tools/shot.cs <picture> [picture ...] [--dir <folder>]");
    return 1;
}
foreach (var picture in pictures)
{
    var name = Path.GetFileName(picture);
    var problem = !File.Exists(picture) ? "not found"
        : name.StartsWith("real-", StringComparison.OrdinalIgnoreCase) ? "a --real shot: it shows your own mods and data"
        : Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg") ? "not a PNG or JPG"
        : name.Equals("image.png", StringComparison.OrdinalIgnoreCase) ? "shrink.cs leaves image.png alone (the Workshop's main picture); rename it"
        // Each becomes <stem>.jpg, so menu.png and menu.jpg would land on the same file.
        : pictures.Count(p => Path.GetFileNameWithoutExtension(p).Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)) > 1
            ? "another picture has the same name"
        : null;
    if (problem != null)
    {
        Console.Error.WriteLine($"{name}: {problem}");
        return 1;
    }
}
folder ??= Git(root, "branch", "--show-current").Trim();
if (folder.Length == 0)
{
    Console.Error.WriteLine("no current branch (detached HEAD): name the folder with --dir");
    return 1;
}
// A relative path of plain names, like a branch name, so nothing lands outside the pr-screenshots checkout.
if (Path.IsPathRooted(folder) || folder.IndexOfAny(['\\', ':']) >= 0 || folder.Split('/').Any(part => part is "" or "." or ".."))
{
    Console.Error.WriteLine($"--dir {folder}: use a relative folder like a branch name (no .., no drive or leading /)");
    return 1;
}

var work = Directory.CreateTempSubdirectory("spire-shots-").FullName;
var tree = Path.Combine(work, "tree");
try
{
    // Copies, so shrink.cs (which replaces a PNG by its JPG) leaves the originals alone.
    var copies = pictures.Select(p => Path.Combine(work, Path.GetFileName(p))).ToList();
    foreach (var (picture, copy) in pictures.Zip(copies))
        File.Copy(picture, copy);
    Run(root, "dotnet", ["run", Path.Combine(root, "workshop", "shrink.cs"), .. copies]);
    var shots = new List<string>();
    foreach (var copy in copies)
    {
        var jpg = Path.ChangeExtension(copy, ".jpg");
        var shot = File.Exists(jpg) ? jpg : copy;
        // shrink.cs leaves a picture as it was when it can't get it under 1 MB, and a JPG that it judged fine.
        if (Path.GetExtension(shot).ToLowerInvariant() == ".png" || new FileInfo(shot).Length >= 1_000_000)
        {
            Console.Error.WriteLine($"{Path.GetFileName(copy)}: couldn't be shrunk under 1 MB; crop it");
            return 1;
        }
        shots.Add(shot);
    }

    Git(root, "fetch", "--quiet", "origin", $"+refs/heads/{Branch}:refs/remotes/origin/{Branch}");
    Git(root, "worktree", "add", "--quiet", "--detach", tree, $"origin/{Branch}");
    var destination = Path.GetFullPath(Path.Combine(tree, folder));
    if (!destination.StartsWith(Path.GetFullPath(tree) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new Exception($"{folder} resolves outside the pr-screenshots checkout");
    Directory.CreateDirectory(destination);
    foreach (var shot in shots)
        File.Copy(shot, Path.Combine(destination, Path.GetFileName(shot)), overwrite: true);
    Git(tree, "add", "--", folder);
    if (Git(tree, "status", "--porcelain").Trim().Length > 0)
    {
        Git(tree, "commit", "--quiet", "-m", $"Screenshots for {folder}");
        // Another branch's pictures may land first; ours are in their own folder, so a rebase never conflicts.
        for (var attempt = 1; ; attempt++)
        {
            var (code, _, error) = Exec(tree, "git", ["push", "--quiet", "origin", $"HEAD:refs/heads/{Branch}"]);
            if (code == 0)
                break;
            if (attempt == 3)
                throw new Exception($"git push failed: {error.Trim()}");
            Git(tree, "fetch", "--quiet", "origin", $"+refs/heads/{Branch}:refs/remotes/origin/{Branch}");
            Git(tree, "rebase", "--quiet", $"origin/{Branch}");
        }
    }

    var repo = Regex.Match(Git(root, "remote", "get-url", "origin").Trim(), @"github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?/?$");
    var url = $"https://github.com/{repo.Groups["repo"].Value}/blob/{Branch}";
    Console.WriteLine();
    foreach (var shot in shots)
    {
        var path = string.Join('/', folder.Split('/').Append(Path.GetFileName(shot)).Select(Uri.EscapeDataString));
        Console.WriteLine($"![{Path.GetFileNameWithoutExtension(shot)}]({url}/{path}?raw=true)");
    }
    return 0;
}
finally
{
    if (Directory.Exists(tree))
        Exec(root, "git", ["worktree", "remove", "--force", tree]);
    Directory.Delete(work, recursive: true);
}

static string Git(string dir, params string[] arguments)
{
    var (code, output, error) = Exec(dir, "git", arguments);
    return code == 0 ? output : throw new Exception($"git {arguments[0]} failed: {error.Trim()}");
}

static void Run(string dir, string file, string[] arguments)
{
    var (code, output, error) = Exec(dir, file, arguments);
    Console.Write(output);
    Console.Error.Write(error);
    if (code != 0)
        throw new Exception($"{file} {arguments[0]} failed");
}

static (int Code, string Output, string Error) Exec(string dir, string file, string[] arguments)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error.Result);
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";
