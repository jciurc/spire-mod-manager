using System.Net;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// A Workshop mod's patch notes in the info panel: its id, author and last update, then each update's notes
// (newest first), read from the Workshop's change notes page (the latest page of them). The panel's other
// parts step aside while it's open; Back returns to the mod. Fetched once per mod per session.
static class PatchNotes
{
    public const string ViewName = "SpireModManagerPatchNotes";
    const string HiddenMeta = "spire_mod_manager_hidden_by_patch_notes";
    static readonly Color Cream = new(1, 0.964706f, 0.886275f);
    const string GoldHex = "#efc851", DimHex = "#fff6e299";

    public record Entry(DateTime Date, string Notes);

    // Self-test only: the page's HTML for a Workshop id, instead of asking Steam.
    public static Func<ulong, Task<string>>? TestFetch;

    static readonly Dictionary<ulong, Task<List<Entry>>> Fetched = [];
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static bool Open(Node info) => info.GetNodeOrNull<Control>(ViewName) is { Visible: true };

    public static void Show(Control info, Mod mod)
    {
        if (mod.workshopId is not { } workshopId)
            return;
        HistoryBar.CloseList(info);
        var view = info.GetNodeOrNull<VBoxContainer>(ViewName) ?? Add(info);
        if (!view.Visible)
        {
            foreach (var child in info.GetChildren().OfType<Control>().Where(c => c != view && c.Visible))
            {
                child.SetMeta(HiddenMeta, true);
                child.Visible = false;
            }
            view.Visible = true;
        }
        var head = view.GetNode<RichTextLabel>("Head");
        var updated = WorkshopInfo.For(mod)?.Updated;
        head.Text = $"[b]{Escape(mod.manifest?.name ?? "")}[/b]\n"
            + $"[color={DimHex}]{Escape(mod.manifest?.id ?? "")}[/color]\n"
            + $"[color={GoldHex}]{Menus.Text("SPIRE_MOD_MANAGER.AUTHOR")}[/color]: {Escape(mod.manifest?.author ?? Menus.Text("SPIRE_MOD_MANAGER.UNKNOWN"))}"
            + (updated is { } date ? $"\n[color={GoldHex}]{Menus.Text("SPIRE_MOD_MANAGER.UPDATED")}[/color]: {ModDetails.Date(date)}" : "");
        var notes = view.GetNode<RichTextLabel>("Notes");
        notes.Text = $"[color={DimHex}]{Menus.Text("SPIRE_MOD_MANAGER.PATCH_NOTES_LOADING")}[/color]";
        view.SetMeta("workshop_id", (long)workshopId);

        Get(workshopId).ContinueWith(done => Callable.From(() =>
        {
            // Still open on this mod?
            if (!GodotObject.IsInstanceValid(view) || !view.Visible || (long)view.GetMeta("workshop_id") != (long)workshopId)
                return;
            notes.Text = done.IsFaulted ? $"[color={DimHex}]{Menus.Text("SPIRE_MOD_MANAGER.PATCH_NOTES_FAILED")}[/color]"
                : done.Result.Count == 0 ? $"[color={DimHex}]{Menus.Text("SPIRE_MOD_MANAGER.PATCH_NOTES_NONE")}[/color]"
                : string.Join("\n\n", done.Result.Select(e => $"[color={GoldHex}][b]{ModDetails.Date(e.Date)}[/b][/color]\n{e.Notes}"));
        }).CallDeferred());
    }

    // Back to the mod (Back, another mod picked, another view or page).
    public static void Close(Node info)
    {
        if (info.GetNodeOrNull<Control>(ViewName) is not { Visible: true } view)
            return;
        view.Visible = false;
        foreach (var child in info.GetChildren().OfType<Control>().Where(c => c.HasMeta(HiddenMeta)))
        {
            child.RemoveMeta(HiddenMeta);
            child.Visible = true;
        }
    }

    static VBoxContainer Add(Control info)
    {
        var view = new VBoxContainer { Name = ViewName, Visible = false };
        view.AddThemeConstantOverride("separation", 10);
        info.AddChild(view);
        view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        (view.OffsetLeft, view.OffsetTop, view.OffsetRight, view.OffsetBottom) = (ModDetails.Inset, 14, -ModDetails.Inset, -16);

        var top = new HBoxContainer();
        var title = new Label { Text = Menus.Text("SPIRE_MOD_MANAGER.PATCH_NOTES"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontOverride("font", ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"));
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", Cream);
        top.AddChild(title);
        var back = Menus.SmallButton(Menus.Text("SPIRE_MOD_MANAGER.BACK_TO_CHANGES"), () => Close(info), name: "PatchNotesBackButton");
        back.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        top.AddChild(back);
        view.AddChild(top);

        var head = new RichTextLabel { Name = "Head", BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        Hover.Fonts(head, 17);
        view.AddChild(head);
        var notes = new RichTextLabel
        {
            Name = "Notes", BbcodeEnabled = true, ScrollActive = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectionEnabled = true,
        };
        Hover.Fonts(notes, 17);
        view.AddChild(notes);
        return view;
    }

    // The updates for a Workshop id: fetched once (again after a failure).
    public static Task<List<Entry>> Get(ulong workshopId)
    {
        if (!Fetched.TryGetValue(workshopId, out var task) || task.IsFaulted)
            Fetched[workshopId] = task = Fetch(workshopId);
        return task;
    }

    static async Task<List<Entry>> Fetch(ulong workshopId)
    {
        string html;
        if (TestFetch != null)
            html = await TestFetch(workshopId);
        else
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"https://steamcommunity.com/sharedfiles/filedetails/changelog/{workshopId}?l=english");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (SpireModManager)");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync();
        }
        return Parse(html);
    }

    // Each update on the page is <p id="<unix time>">notes</p>; the notes are Steam's HTML for its BBCode.
    static readonly Regex Update = new(@"<p id=""(\d+)"">(.*?)</p>\s*(?:<div class=""commentsLink|</div>)", RegexOptions.Singleline);

    public static List<Entry> Parse(string html) =>
        Update.Matches(html).Select(m => new Entry(DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).LocalDateTime, ToBbcode(m.Groups[2].Value)))
            .ToList();

    // Headings bold, list items as bullets, links as their text, line breaks kept; everything else plain.
    public static string ToBbcode(string html)
    {
        var text = html.Replace("[", "[lb]").Replace("\r", "").Replace("\n", "");
        text = Regex.Replace(text, @"(</div>)\s*<br\s*/?>", "$1"); // a heading's own line break
        text = Regex.Replace(text, @"<div class=""bb_h1"">(.*?)</div>", "[font_size=22][b]$1[/b][/font_size]\n");
        text = Regex.Replace(text, @"<div class=""bb_h[23]"">(.*?)</div>", "[b]$1[/b]\n");
        text = Regex.Replace(text, @"<li>", "  •  ");
        text = Regex.Replace(text, @"</li>|<br\s*/?>", "\n");
        text = Regex.Replace(text, @"<(b|strong)>(.*?)</\1>", "[b]$2[/b]");
        text = Regex.Replace(text, @"<(i|em)>(.*?)</\1>", "[i]$2[/i]");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\n\s*\n(\s*\n)+", "\n\n"); // at most one blank line
        text = Regex.Replace(text, @"\n\n(?=  •  )", "\n"); // no blank line inside a list
        return text.Trim();
    }

    static string Escape(string text) => text.Replace("[", "[lb]");
}
