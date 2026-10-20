// Watchdog for the public repository: counts what outside accounts opened and commented in the last hour. On a flood
// it locks the conversations they touched to collaborators and opens a "watchdog" issue for the maintainer; it never
// closes anything or reads what they wrote. watchdog.yml runs it every 30 minutes once the variable WATCHDOG is "on".
//
// Usage: dotnet run tools/watchdog.cs [--dry-run] [--hours 1] [--repo owner/name]
//   --dry-run: report only, change nothing. --count-everyone (dry runs only): count collaborators too, to see
//   a trip without a flood.
using System.Diagnostics;
using System.Globalization;
using System.Text;

const int MaxItems = 5;     // pull requests and issues opened by outsiders in the window
const int MaxComments = 30; // their comments, on conversations and inline in reviews, in the window

var dryRun = args.Contains("--dry-run");
var countEveryone = args.Contains("--count-everyone");
if (countEveryone && !dryRun)
{
    Console.Error.WriteLine("--count-everyone is for dry runs only");
    return 1;
}
var hours = Arg("--hours") is { } h ? double.Parse(h, CultureInfo.InvariantCulture) : 1;
var repo = Arg("--repo") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")
    ?? Gh("repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner").Trim();
var since = DateTimeOffset.UtcNow.AddHours(-hours);
var sinceText = since.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

// Each row: number, created_at, login, user type, author association. "since" filters on the last update, so
// rows are kept only when they were created in the window.
var items = Rows($"repos/{repo}/issues?state=all&since={sinceText}&per_page=100",
        ".[] | [.number, .created_at, .user.login, .user.type, .author_association] | @tsv")
    .Where(Counted).ToList();
var comments = Rows($"repos/{repo}/issues/comments?since={sinceText}&per_page=100",
        ".[] | [(.issue_url | split(\"/\") | last), .created_at, .user.login, .user.type, .author_association] | @tsv")
    .Concat(Rows($"repos/{repo}/pulls/comments?since={sinceText}&per_page=100",
        ".[] | [(.pull_request_url | split(\"/\") | last), .created_at, .user.login, .user.type, .author_association] | @tsv"))
    .Where(Counted).ToList();

var who = items.Concat(comments).GroupBy(row => row[2]).OrderByDescending(g => g.Count())
    .Select(g => $"`{g.Key}` ({g.Count()})").ToList();
Console.WriteLine($"{repo}, last {hours:0.##} h: {items.Count} pull requests and issues, {comments.Count} comments from outsiders"
    + $" (limits {MaxItems} and {MaxComments}){(who.Count > 0 ? ": " + string.Join(", ", who) : "")}");
if (items.Count <= MaxItems && comments.Count <= MaxComments)
{
    Console.WriteLine("quiet");
    return 0;
}

var locked = new List<string>();
foreach (var number in items.Concat(comments).Select(row => row[0]).Distinct().OrderBy(int.Parse))
{
    var state = Gh("api", $"repos/{repo}/issues/{number}", "--jq", "[.state, .locked] | @tsv").Trim().Split('\t');
    if (state[0] != "open" || state[1] == "true")
        continue;
    if (!dryRun)
        Gh("api", "-X", "PUT", $"repos/{repo}/issues/{number}/lock", "-f", "lock_reason=spam");
    locked.Add($"#{number}");
}

var body = new StringBuilder()
    .AppendLine($"In the last {hours:0.##} h, outside accounts opened {items.Count} pull requests and issues and left {comments.Count} comments,"
        + $" over the watchdog's limits ({MaxItems} and {MaxComments}).")
    .AppendLine()
    .AppendLine($"Most active: {string.Join(", ", who.Take(10))}")
    .AppendLine()
    .AppendLine(locked.Count > 0
        ? $"Locked, so only collaborators can comment there: {string.Join(", ", locked)}"
        : "Nothing open left to lock.")
    .AppendLine()
    .AppendLine("Further switches, from a terminal logged in as the owner:")
    .AppendLine($"- Only collaborators can open or comment on anything, for a day (or `three_days`, `one_week`, `one_month`, `six_months`): `gh api -X PUT repos/{repo}/interaction-limits -f limit=collaborators_only -f expiry=one_day`. Undo: `gh api -X DELETE repos/{repo}/interaction-limits`")
    .AppendLine($"- Unlock a conversation: `gh api -X DELETE repos/{repo}/issues/<number>/lock`")
    .AppendLine($"- Pause the watchdog: `gh variable set WATCHDOG --body off --repo {repo}`")
    .AppendLine()
    .AppendLine("Close this issue once it's handled. While it's open, later trips add a comment here instead of a new issue.")
    .ToString();
if (dryRun)
{
    Console.WriteLine($"dry run, nothing changed. Would lock: {(locked.Count > 0 ? string.Join(", ", locked) : "nothing")}. Would report:\n\n{body}");
    return 0;
}

var open = Gh("api", $"repos/{repo}/issues?labels=watchdog&state=open&per_page=1", "--jq", ".[0].number // empty").Trim();
if (open.Length > 0)
{
    Gh("api", $"repos/{repo}/issues/{open}/comments", "-f", $"body={body}");
    Console.WriteLine($"tripped: locked {locked.Count}, added to #{open}");
    return 0;
}
Exec("gh", ["api", $"repos/{repo}/labels", "-f", "name=watchdog", "-f", "color=b60205",
    "-f", "description=Opened by the watchdog: a flood of outside activity"]); // fails harmlessly when it exists
string[] create = ["api", $"repos/{repo}/issues", "-f", "title=Watchdog: a flood of outside activity", "-f", $"body={body}",
    "-f", "labels[]=watchdog", "--jq", ".number"];
// Assigned to the owner so GitHub notifies them; an organization owner can't be assigned, so then it goes without.
var (code, created, _) = Exec("gh", [.. create, "-f", $"assignees[]={repo.Split('/')[0]}"]);
if (code != 0)
    created = Gh(create);
Console.WriteLine($"tripped: locked {locked.Count}, opened #{created.Trim()}");
return 0;

bool Counted(string[] row) =>
    DateTimeOffset.Parse(row[1], CultureInfo.InvariantCulture) >= since && row[3] != "Bot"
    && (countEveryone || row[4] is not ("OWNER" or "MEMBER" or "COLLABORATOR"));

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static IEnumerable<string[]> Rows(string path, string jq) =>
    Gh("api", "--paginate", path, "--jq", jq).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'));

static string Gh(params string[] arguments)
{
    var (code, output, error) = Exec("gh", arguments);
    return code == 0 ? output : throw new Exception($"gh {string.Join(' ', arguments.Take(3))} failed: {error.Trim()}");
}

static (int Code, string Output, string Error) Exec(string file, string[] arguments)
{
    var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {file}");
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error.Result);
}
