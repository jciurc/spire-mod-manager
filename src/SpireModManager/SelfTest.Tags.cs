using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace SpireModManager;

// The self-test's checks of tags: the suggestions as you type, and the tags box's pills.
static partial class SelfTest
{
    // Tag suggestions, with made-up tags on made-up mods so the counts are known (the data is put back after): the
    // panel's tags box lists your tags, most used first, at most 6; typing narrows them (starting with the word,
    // then containing it), leaving out the ones in the box; Down lights one, Enter, a comma or a click takes it;
    // Escape closes the list but stays in the box; nothing matching or leaving the box closes it. The bulk menu's
    // box too, the list drawn over the menu; Tab takes one there. Keys go through Godot's input, as a player's do.
    static async Task TestTagSuggestions(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        NModMenuRow? Row(string id) => content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == id);
        if (Row("SmmTestUi") is not { } ui || Row("SmmTestLib") is not { } lib || Row("SmmTestContent") is not { } other)
        {
            Results.Add("SKIP tag suggestions: test mods not installed");
            return;
        }
        var before = Store.EditsJson();
        Store.Batch(() =>
        {
            foreach (var info in Store.Data.Mods.Values)
                info.Tags = null;
            // Must-have twice on one mod, in two cases (as a hand-edited file can have it): still one mod, behind the
            // tags on two.
            Store.EditMod("SmmSuggestA", info => info.Tags = ["Strategy", "Story-heavy", "Must-have", "must-have", "Co-op"]);
            Store.EditMod("SmmSuggestB", info => info.Tags = ["Strategy", "Story-heavy", "Co-op"]);
            Store.EditMod("SmmSuggestC", info => info.Tags = ["Strategy", "Stable", "Fast", "Visuals", "Tools"]);
        });
        async Task Press(Key key, char unicode = '\0')
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = unicode, Pressed = true });
            await Wait(menu, 0.1);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = unicode, Pressed = false });
            await Wait(menu, 0.1);
        }
        async Task TypeIn(LineEdit box, string text)
        {
            box.Text = text;
            box.CaretColumn = text.Length;
            box.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Wait(menu, 0.2);
        }
        string Listed() => string.Join(", ", TagSuggestions.Labels());
        // Under the box (or above it, short of room below), as wide, on screen, on a layer over `over`.
        string Placement(LineEdit box, int over)
        {
            if (TagSuggestions.Current is not { } layer || layer.GetNodeOrNull<Control>("Panel") is not { } list)
                return "no list";
            var (shown, at) = (list.GetGlobalRect(), box.GetGlobalRect());
            var ok = (shown.Position.Y >= at.End.Y || shown.End.Y <= at.Position.Y) && Math.Abs(shown.Position.X - at.Position.X) < 1
                && Math.Abs(shown.Size.X - at.Size.X) < 1 && list.GetViewportRect().Encloses(shown) && layer.Layer > over;
            return ok ? "ok" : $"list {shown} on layer {layer.Layer}, box {at}";
        }

        CategoryList.Select(screen, ui);
        await PanelFor(menu, screen, ui);
        await Wait(menu, 0.5);
        var box = screen.GetNode<LineEdit>("%ModInfoContainer/SpireModManagerYours/Tags/Box");
        box.GrabFocus();
        await Wait(menu, 0.3);
        Check("tag suggestions: focusing the tags box lists your tags, on the most mods first (a mod counts once), at most 6", Listed() == "Strategy, Co-op, Story-heavy, Fast, Must-have, Stable", Listed());
        var placed = Placement(box, 90); // MenuPanel's layer
        Check("tag suggestions: under the box, as wide, on screen, over our menus", placed == "ok", placed);
        await TypeIn(box, "st");
        Check("tag suggestions: typing narrows them, tags starting with the word first, then ones containing it", Listed() == "Strategy, Story-heavy, Stable, Fast, Must-have", Listed());
        await Capture(screen, outDir, "19-tag-suggestions");
        await Press(Key.Down);
        Check("tag suggestions: Down lights the first", TagSuggestions.Highlighted == "Strategy", TagSuggestions.Highlighted ?? "none lit");
        await Press(Key.Enter);
        Check("tag suggestions: Enter takes it in place of the word, then \", \"; the list stays, without it", box.Text == "Strategy, " && box.HasFocus()
            && TagSuggestions.Current != null && !TagSuggestions.Labels().Contains("Strategy"), $"\"{box.Text}\": {Listed()}");
        await TypeIn(box, "Strategy, sto");
        await Press(Key.Down);
        await Press(Key.Comma, ',');
        Check("tag suggestions: a comma takes the lit one", box.Text == "Strategy, Story-heavy, ", box.Text);
        await Press(Key.Escape);
        Check("tag suggestions: Escape closes the list, staying in the box", TagSuggestions.Current == null && box.HasFocus() && box.Text == "Strategy, Story-heavy, ", box.Text);
        await TypeIn(box, "Strategy, Story-heavy, zzz");
        Check("tag suggestions: none when nothing matches", TagSuggestions.Current == null, Listed());
        await TypeIn(box, "Strategy, Story-heavy, f");
        Check("tag suggestions: typing brings the list back", Listed() == "Fast", Listed());
        Check("tag suggestions: a heading over them says what they are and how to take several",
            TagSuggestions.Heading() == "Tags in use; Shift-click to add multiple", TagSuggestions.Heading());
        TagSuggestions.Press("Fast", shift: true);
        await Wait(menu, 0.2);
        Check("tag suggestions: a Shift-click takes one, the box keeps focus and the list stays for more", box.Text == "Strategy, Story-heavy, Fast, " && box.HasFocus()
            && TagSuggestions.Current != null && TagSuggestions.Labels().Contains("Co-op"), $"\"{box.Text}\": {Listed()}");
        TagSuggestions.Press("Co-op");
        await Wait(menu, 0.3);
        Check("tag suggestions: a click takes one and finishes: the box left, the list closed, the tags saved, tidied", TagSuggestions.Current == null && !box.HasFocus()
            && Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["Strategy", "Story-heavy", "Fast", "Co-op"]) == true, string.Join(", ", Store.InfoFor("SmmTestUi")?.Tags ?? []));
        // Another mod picked while the box is open (its Go to, say): the typing saved for the mod it was typed for, the box
        // closed showing the new mod's tags, and nothing of it saved over theirs when the focus goes (Jordan, 28 Oct)
        var libId = lib.Mod?.manifest?.id ?? "";
        var libTags = string.Join(", ", Store.InfoFor(libId)?.Tags ?? []);
        box.GrabFocus();
        await TypeIn(box, "Strategy, Story-heavy, Fast, Co-op, Carried");
        CategoryList.Select(screen, lib);
        await PanelFor(menu, screen, lib);
        await Wait(menu, 0.3);
        var (open, shownTags) = (box.HasFocus(), box.Text);
        box.GrabFocus();
        box.ReleaseFocus();
        await Wait(menu, 0.2);
        Check("a panel field open when another mod is picked: its typing saved for its own mod, the box closed with the new mod's value, which leaving it "
            + "doesn't overwrite", !open && shownTags == libTags && string.Join(", ", Store.InfoFor(libId)?.Tags ?? []) == libTags
            && Store.InfoFor("SmmTestUi")?.Tags?.Contains("Carried") == true, $"open {open}, \"{shownTags}\" for \"{libTags}\"; now {string.Join(", ", Store.InfoFor(libId)?.Tags ?? [])}; "
            + $"ui {string.Join(", ", Store.InfoFor("SmmTestUi")?.Tags ?? [])}");
        Store.EditMod("SmmTestUi", info => info.Tags = ["Strategy", "Story-heavy", "Fast", "Co-op"]);

        Selection.Clear(content);
        Selection.Add(content, [lib, other]);
        // The menu opens at the mouse: over the second row, as a right-click there would. A headless run has no mouse of
        // its own (its place is whatever the popups before left, out to infinity and past it).
        Hover.TestMouse = () => other.GetGlobalRect().GetCenter();
        var opened = Selection.Mods.Count == 2 ? CategoryList.ShowBulkMenu(content) : null;
        Hover.TestMouse = null;
        if (opened is CanvasLayer bulk && bulk.FindChild("Tags", true, false)?.GetNodeOrNull<LineEdit>("Box") is { } bulkBox)
        {
            await Wait(menu, 0.3);
            bulkBox.GrabFocus();
            await TypeIn(bulkBox, "to");
            var overMenu = Placement(bulkBox, bulk.Layer);
            Check("tag suggestions: the bulk menu's tags box too, the list over the menu", Listed() == "Tools, Story-heavy" && overMenu == "ok", $"{Listed()}; {overMenu}");
            await Capture(screen, outDir, "19b-tag-suggestions-bulk");
            await Press(Key.Down);
            await Press(Key.Tab);
            Check("tag suggestions: Tab takes the lit one", bulkBox.Text == "Tools, " && bulkBox.HasFocus(), bulkBox.Text);
            MenuPanel.Close();
            await Wait(menu, 0.3);
            Check("tag suggestions: closing the menu takes the list away; the tag goes on both", TagSuggestions.Current == null
                && new[] { "SmmTestLib", "SmmTestContent" }.All(id => Store.InfoFor(id)?.Tags?.SequenceEqual(["Tools"]) == true),
                string.Join(" | ", new[] { "SmmTestLib", "SmmTestContent" }.Select(id => string.Join(",", Store.InfoFor(id)?.Tags ?? []))));
        }
        else
            Results.Add("FAIL tag suggestions: no bulk menu with a tags box");
        Selection.Clear(content);
        Store.RestoreEdits(before);
        CategoryList.Apply(content);
        ModDetails.Refresh(screen);
        await Wait(menu, 0.3);
    }

    // Tag pills (TagPills) in the panel's tags box, with made-up tags (the data is put back after): finished tags drawn
    // as pills where the box draws its text, their text smaller unless the caret or a selection is at one (a click on
    // one lands in its tag and opens it), the word being typed plain, the colours, the commas invisible;
    // a typed comma makes a pill; Backspace and Delete by Jordan's rule (a fresh press next to a pill takes it and its
    // separator as one undo step, a held key's repeats stop at it); Copy, Cut and Paste of whole tags; a double-click
    // takes the whole tag; all of that after an emoji too, and the pills with a right-to-left tag first; a comma the full
    // box has no room for takes no undo step; Ctrl+Z takes back a picked suggestion; a long list scrolls, its pills with
    // it; leaving the box still tidies. Keys and clicks go through Godot's input as a player's do (a held key's repeats are Echo
    // events), each edit landed at its frame's end before the next; the clipboard is the test's own.
    static async Task TestTagPills(Node menu, NModdingScreen screen, Control content, string outDir)
    {
        if (content.GetChildren().OfType<NModMenuRow>().FirstOrDefault(r => r.Mod?.manifest?.id == "SmmTestUi") is not { } ui)
        {
            Results.Add("SKIP tag pills: test mods not installed");
            return;
        }
        var before = Store.EditsJson();
        try
        {
            Store.Batch(() =>
            {
                Store.EditMod("SmmTestUi", info => info.Tags = null); // its box showing, not its pills at rest
                Store.EditMod("SmmPillsA", info => info.Tags = ["Zebra-stripes"]);
            });
            CategoryList.Select(screen, ui);
            await PanelFor(menu, screen, ui);
            await Wait(menu, 0.5);
            var box = screen.GetNode<LineEdit>("%ModInfoContainer/SpireModManagerYours/Tags/Box");
            box.GrabFocus();
            await Wait(menu, 0.2);
            if (!box.HasFocus() || !box.IsEditing())
            {
                Results.Add($"FAIL tag pills: the tags box didn't take the keyboard (focus {box.HasFocus()}, editing {box.IsEditing()})");
                return;
            }
            TagPills.TestClipboard = "";
            var cream = new Color(1, 0.964706f, 0.886275f);
            var inBox = new Rect2(Vector2.Zero, box.Size);
            string Pills() => string.Join(", ", TagPills.TestPills(box).Select(p => p.Tag));
            string Say() => $"\"{box.Text}\" caret {box.CaretColumn}, pills: {Pills()}";
            async Task Typed(string text, int? caret = null)
            {
                box.Text = text;
                box.CaretColumn = caret ?? TagPills.Columns(text);
                box.EmitSignal(LineEdit.SignalName.TextChanged, text);
                await Wait(menu, 0.05);
            }
            async Task Send(Key key, bool pressed, bool echo = false, bool ctrl = false, char unicode = '\0')
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = unicode, CtrlPressed = ctrl, Pressed = pressed, Echo = echo });
                await Wait(menu, 0.05);
            }
            async Task Tap(Key key, bool ctrl = false, char unicode = '\0')
            {
                await Send(key, true, ctrl: ctrl, unicode: unicode);
                await Send(key, false, ctrl: ctrl, unicode: unicode);
            }
            async Task Repeats(Key key, int count) // a held key, after its press
            {
                for (var i = 0; i < count; i++)
                    await Send(key, true, echo: true);
            }
            // A click at a point in the box, through the viewport as a player's (works headless).
            async Task ClickAt(Vector2 local, bool doubleClick = false)
            {
                var at = ViewportPoint(box, box.GetGlobalTransform() * local);
                foreach (var pressed in new[] { true, false })
                {
                    box.GetViewport().PushInput(new InputEventMouseButton
                        { ButtonIndex = MouseButton.Left, Pressed = pressed, DoubleClick = doubleClick && pressed, Position = at, GlobalPosition = at }, true);
                    await Wait(menu, 0.05);
                }
            }

            await Typed("co-op, Favorites, wor");
            var drawn = TagPills.TestPills(box);
            Check("tag pills: finished tags are pills, left to right inside the box; the word being typed isn't one",
                Pills() == "co-op, Favorites" && drawn[0].Rect.End.X < drawn[1].Rect.Position.X && drawn.All(p => inBox.Encloses(p.Rect)),
                $"{Say()}: {string.Join(", ", drawn.Select(p => p.Rect))} in {inBox}");
            var ink = TagPills.TestInk(box);
            var (boxSize, pillSize) = (box.GetThemeFontSize("font_size"), box.GetThemeFontSize("font_size") - TagPills.Smaller);
            (string, Color, int)[] inks = [("co-op", Tags.CustomColor, pillSize), (", ", new Color(cream, 0), boxSize), ("Favorites", Tags.CustomColor, pillSize),
                (", ", new Color(cream, 0), boxSize), ("wor", cream, boxSize)];
            var ownInk = box.GetThemeColor("font_color");
            box.Select(7, 16);
            await Wait(menu, 0.05);
            var unselected = string.Concat(TagPills.TestInk(box).Select(r => r.Text));
            var selectedPill = TagPills.TestPills(box).ElementAtOrDefault(1).Rect;
            box.Deselect();
            Check("tag pills: tag text in the tag colour and smaller, the commas invisible (alpha 0), the word being typed cream at the box's size, "
                + "over the box's own text (clear while it's typed in); a selected tag is left to the box", ink.Count == inks.Length
                && ink.Zip(inks).All(r => r.First.Text == r.Second.Item1 && r.First.Color.IsEqualApprox(r.Second.Item2) && r.First.Size == r.Second.Item3)
                && ownInk.A == 0 && unselected == "co-op, , wor",
                $"{string.Join(" | ", ink.Select(r => $"\"{r.Text}\" {r.Color} {r.Size}px"))}; the box's own {ownInk}; with Favorites selected: \"{unselected}\"");
            // The box lays its caret and selection out at its own size, so the pill the caret is in, or a selection
            // touches, is drawn at that size, the others smaller.
            box.CaretColumn = 10; // in "Favorites"
            await Wait(menu, 0.05);
            var (openPills, openInk) = (TagPills.TestPills(box), TagPills.TestInk(box));
            Check("tag pills: a pill with the caret in it, or a selection touching it, is drawn at the box's size, the others smaller",
                openPills is [var openCoop, var openFavorites] && drawn is [var smallCoop, var smallFavorites] && openCoop.Rect == smallCoop.Rect
                && openFavorites.Rect.Size.X > smallFavorites.Rect.Size.X && openFavorites.Rect == selectedPill
                && openInk.Find(r => r.Text == "Favorites").Size == boxSize && openInk.Find(r => r.Text == "co-op").Size == pillSize,
                $"{string.Join(", ", openPills.Select(p => $"{p.Tag} {p.Rect}"))} (smaller {string.Join(", ", drawn.Select(p => p.Rect))}, selected {selectedPill}); "
                + string.Join(" | ", openInk.Select(r => $"\"{r.Text}\" {r.Size}px")));

            // Where the pills are drawn, by where the box puts the caret for a click: a click in a smaller pill's middle
            // lands inside its tag and opens the pill to the box's size; then 1 px into each end of its tag lands on that
            // end (more than about half a letter of drift would miss it), its middle inside it. In the box's columns (an
            // emoji is one, and two chars of the C# string).
            async Task<List<string>> Misses(bool ends = true)
            {
                var list = new List<string>();
                foreach (var (tag, smaller) in TagPills.TestPills(box)) // the caret at the end: in none of them
                {
                    var start = TagPills.Column(box.Text, box.Text.IndexOf(tag, StringComparison.Ordinal));
                    var end = start + TagPills.Columns(tag);
                    await ClickAt(smaller.GetCenter());
                    var inSmaller = box.CaretColumn;
                    var rect = TagPills.TestPills(box).FirstOrDefault(p => p.Tag == tag).Rect;
                    var middle = rect.GetCenter().Y;
                    var (left, right) = (start, end);
                    if (ends)
                    {
                        await ClickAt(new Vector2(rect.Position.X + TagPills.PadLeft + 1, middle));
                        left = box.CaretColumn;
                        await ClickAt(new Vector2(rect.End.X - TagPills.PadRight - 1, middle));
                        right = box.CaretColumn;
                    }
                    await ClickAt(rect.GetCenter());
                    if (inSmaller <= start || inSmaller >= end || rect.Size.X <= smaller.Size.X || left != start || right != end
                        || box.CaretColumn <= start || box.CaretColumn >= end)
                        list.Add($"{tag} ({start}..{end}) at {smaller}: middle {inSmaller}; opened at {rect}: {left}, {right}, middle {box.CaretColumn}");
                }
                return list;
            }
            await Typed("co-op, Favorites, Story-heavy, wor");
            var missed = await Misses();
            Check("tag pills: drawn where the box draws the text: a click at either end of a pill's tag puts the caret there, in its middle inside it",
                TagPills.TestPills(box).Count == 3 && missed.Count == 0, missed.Count > 0 ? string.Join("; ", missed) : Say());
            if (TagPills.TestPills(box) is [_, (_, var favorites), ..])
            {
                await ClickAt(favorites.GetCenter());
                await Capture(screen, outDir, "19c-tag-pills-caret"); // the caret between two of our letters
            }
            if (TagPills.TestPills(box) is [.., (_, var storyHeavy)])
                await ClickAt(new Vector2(storyHeavy.Position.X + storyHeavy.Size.X / 4, storyHeavy.GetCenter().Y), doubleClick: true); // on "Story"
            Check("tag pills: a double-click selects the whole tag", box.GetSelectedText() == "Story-heavy", $"\"{box.GetSelectedText()}\" selected");
            await WaitReal(menu, 0.7); // not a triple-click with the next
            box.Deselect();

            // An emoji is one column of the box but two chars of the C# string: after one, the pills are still where the
            // box draws the text, and the keys, a double-click and a picked suggestion take the right tag. A right-to-left
            // tag first: the box lays the line out right to left (its text direction, Auto), the pills with it (a click
            // at the edge of a right-to-left run can land either side of it, so only the middles there).
            await Typed("🎮 games, co-op, Favorites, wor");
            var missedAfterEmoji = await Misses();
            Check("tag pills: after an emoji, still drawn where the box draws the text: a click at either end of a pill's tag puts the caret there, in its middle inside it",
                TagPills.TestPills(box).Count == 3 && missedAfterEmoji.Count == 0, missedAfterEmoji.Count > 0 ? string.Join("; ", missedAfterEmoji) : Say());
            await Typed("מועדפים, co-op, Favorites, wor");
            var missedRtl = await Misses(ends: false);
            Check("tag pills: a right-to-left tag first: the pills go where the box lays the line out right to left (a click in a pill's middle lands inside its tag)",
                TagPills.TestPills(box).Count == 3 && missedRtl.Count == 0, missedRtl.Count > 0 ? string.Join("; ", missedRtl) : Say());
            await Typed("🎮 games, B, C, ");
            if (TagPills.TestPills(box) is [(_, var games), ..])
                await ClickAt(new Vector2(games.Position.X + games.Size.X * 3 / 4, games.GetCenter().Y), doubleClick: true); // on "games"
            Check("tag pills: after an emoji, a double-click selects the whole tag, the emoji with it", box.GetSelectedText() == "🎮 games",
                $"\"{box.GetSelectedText()}\" selected; {Say()}");
            await WaitReal(menu, 0.7); // not a triple-click with the next
            box.Deselect();
            await Typed("🎮, A, B, ");
            await Tap(Key.Backspace);
            Check("tag pills: after an emoji, a fresh Backspace at the end takes the last pill and its separator", (box.Text, box.CaretColumn) == ("🎮, A, ", 6), Say());
            await Typed("🎮 games, B, C, ", caret: 12); // just before "C"
            await Tap(Key.Backspace);
            Check("tag pills: after an emoji, a Backspace just before a pill takes the one before it", (box.Text, box.CaretColumn) == ("🎮 games, C, ", 9), Say());
            await Typed("🎮, A, B, ", caret: 3); // at the start of "A"
            await Tap(Key.Delete);
            Check("tag pills: after an emoji, a Delete at a pill's start takes that pill and its separator", (box.Text, box.CaretColumn) == ("🎮, B, ", 3), Say());
            await Typed("🎮, A, B, ");
            await Tap(Key.A, ctrl: true);
            var all = box.GetSelectedText();
            await Tap(Key.X, ctrl: true);
            Check("tag pills: after an emoji, Ctrl+A then Ctrl+X cuts every tag", box.Text == "" && TagPills.TestClipboard == "🎮, A, B",
                $"\"{all}\" selected, then {Say()}, clipboard \"{TagPills.TestClipboard}\"");
            await Typed("🎮, zebr");
            await Tap(Key.Down);
            var litAfterEmoji = TagSuggestions.Highlighted;
            await Tap(Key.Enter);
            Check("tag pills: after an emoji, a picked suggestion takes the place of the word being typed", box.Text == "🎮, Zebra-stripes, ",
                $"lit {litAfterEmoji}: {Say()}");

            await Typed("co-op, Favorites, wor");
            await Tap(Key.Comma, unicode: ',');
            var comma = (box.Text, Pills());
            var gaps = TagPills.TestPills(box).Zip(TagPills.TestPills(box).Skip(1), (a, b) => b.Rect.Position.X - a.Rect.End.X).ToList();
            await Typed("co-op, Fast");
            await Tap(Key.Comma, unicode: '，');
            Check("tag pills: a typed comma, or a full-width one, makes a pill: \", \" after its tag, a gap before the next",
                comma == ("co-op, Favorites, wor, ", "co-op, Favorites, wor") && gaps.Count == 2 && gaps.All(g => g >= 2) && box.Text == "co-op, Fast, ",
                $"{comma}, gaps {string.Join(", ", gaps.Select(g => $"{g:0.0}"))}; then \"{box.Text}\"");

            // Backspace: held from the end of the word being typed, it takes the word and stops at the pill; a fresh press
            // there takes the pill and its ", " (one undo step), and holding on stops at the next.
            await Typed("co-op, Favorites, wor");
            await Send(Key.Backspace, true);
            await Repeats(Key.Backspace, 6);
            var held = (box.Text, box.CaretColumn);
            await Send(Key.Backspace, false);
            await Send(Key.Backspace, true);
            var fresh = box.Text;
            await Repeats(Key.Backspace, 3);
            var heldOn = box.Text;
            await Send(Key.Backspace, false);
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: a held Backspace stops at the first pill (the word being typed goes, the pill stays)", held == ("co-op, Favorites, ", 18), $"{held}");
            Check("tag pills: a fresh Backspace there takes the whole pill and its separator, and one Ctrl+Z puts it back",
                fresh == "co-op, " && box.Text == "co-op, Favorites, ", $"\"{fresh}\", then \"{box.Text}\"");
            Check("tag pills: Backspace held on after that press stops at the next pill", heldOn == "co-op, ", heldOn);
            // Delete, its mirror: held from inside a tag, it takes the letters after the caret and stops at the separator
            // before the next pill; a fresh press there takes that separator and pill, and holding on stops at the next.
            await Typed("co-op, Favorites, Story-heavy, Fast, wor", caret: 10); // after "Fav"
            await Send(Key.Delete, true);
            await Repeats(Key.Delete, 8);
            var heldDelete = (box.Text, box.CaretColumn);
            await Send(Key.Delete, false);
            await Send(Key.Delete, true);
            var freshDelete = (box.Text, box.CaretColumn);
            await Repeats(Key.Delete, 3);
            var heldOnDelete = box.Text;
            await Send(Key.Delete, false);
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: a held Delete stops at the first pill (the tag's letters after the caret go, the next pill stays)",
                heldDelete == ("co-op, Fav, Story-heavy, Fast, wor", 10), $"{heldDelete}");
            Check("tag pills: a fresh Delete there takes the whole pill and its separator, and one Ctrl+Z puts it back",
                freshDelete == ("co-op, Fav, Fast, wor", 10) && box.Text == "co-op, Fav, Story-heavy, Fast, wor", $"{freshDelete}, then \"{box.Text}\"");
            Check("tag pills: Delete held on after that press stops at the next pill", heldOnDelete == "co-op, Fav, Fast, wor", heldOnDelete);
            // At a pill's own edge (Jordan, 21 Oct): Delete at its start, or Backspace at its end, takes it whole with the
            // separator after it, one undo step; held there, it stops (nothing goes).
            await Typed("co-op, Favorites, wor", caret: 7); // at the start of "Favorites"
            await Send(Key.Delete, true);
            var deleteAtStart = (box.Text, box.CaretColumn);
            await Send(Key.Delete, false);
            await Tap(Key.Z, ctrl: true);
            var deleteUndone = box.Text;
            await Typed("co-op, Favorites, wor", caret: 7);
            await Repeats(Key.Delete, 3);
            var deleteHeld = box.Text;
            await Send(Key.Delete, false);
            Check("tag pills: a fresh Delete at a pill's start takes the whole pill and its separator (Ctrl+Z puts it back); held there, nothing goes",
                deleteAtStart == ("co-op, wor", 7) && deleteUndone == "co-op, Favorites, wor" && deleteHeld == "co-op, Favorites, wor",
                $"{deleteAtStart}, undone \"{deleteUndone}\", held \"{deleteHeld}\"");
            await Typed("co-op, Favorites, wor", caret: 16); // at the end of "Favorites"
            await Send(Key.Backspace, true);
            var backAtEnd = (box.Text, box.CaretColumn);
            await Send(Key.Backspace, false);
            await Typed("co-op, Favorites, wor", caret: 16);
            await Repeats(Key.Backspace, 3);
            var backHeld = box.Text;
            await Send(Key.Backspace, false);
            Check("tag pills: a fresh Backspace at a pill's end takes the whole pill and its separator; held there, nothing goes",
                backAtEnd == ("co-op, wor", 7) && backHeld == "co-op, Favorites, wor", $"{backAtEnd}, held \"{backHeld}\"");

            await Typed("co-op, Favorites, ");
            await Tap(Key.Backspace, ctrl: true);
            var wordBack = box.Text;
            await Typed("co-op, Favorites, wor", caret: 5);
            await Tap(Key.Delete, ctrl: true);
            Check("tag pills: Ctrl+Backspace and Ctrl+Delete next to a pill take it whole too", wordBack == "co-op, " && box.Text == "co-op, wor",
                $"\"{wordBack}\", \"{box.Text}\"");

            // Copy, Cut and Paste of whole tags (part of a tag is the box's own).
            await Typed("co-op, Favorites, wor");
            box.Select(0, 5);
            await Tap(Key.C, ctrl: true);
            var one = TagPills.TestClipboard;
            box.Select(0, 16);
            await Tap(Key.C, ctrl: true);
            var two = TagPills.TestClipboard;
            await Typed("co-op, Favorites, ");
            box.Select(7, 16);
            await Tap(Key.C, ctrl: true);
            Check("tag pills: Ctrl+C on whole tags copies them with a comma after when another value follows, the last one without; "
                + "the box's own drag and right-click menu are off (their copies wouldn't keep to that)",
                one == "co-op," && two == "co-op, Favorites," && TagPills.TestClipboard == "Favorites" && !box.DragAndDropSelectionEnabled && !box.ContextMenuEnabled,
                $"\"{one}\", \"{two}\", \"{TagPills.TestClipboard}\"; drag {box.DragAndDropSelectionEnabled}, menu {box.ContextMenuEnabled}");
            await Typed("co-op, Favorites, wor");
            box.Select(7, 16);
            await Tap(Key.X, ctrl: true);
            var cut = (box.Text, TagPills.TestClipboard);
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: Ctrl+X takes the pill and its separator; Ctrl+Z puts it back", cut == ("co-op, wor", "Favorites,") && box.Text == "co-op, Favorites, wor",
                $"{cut}, then \"{box.Text}\"");
            TagPills.TestClipboard = "x, y\nz";
            await Typed("");
            await Tap(Key.V, ctrl: true);
            var pasted = (box.Text, Pills());
            TagPills.TestClipboard = "Co-op, Fast";
            await Typed("co-op, ");
            await Tap(Key.V, ctrl: true);
            var skipped = box.Text;
            await Tap(Key.Z, ctrl: true);
            var unpasted = box.Text;
            var full = string.Join(", ", Enumerable.Range(1, 23).Select(i => $"tag{i:00}")) + ", "; // 161 of the box's 200
            TagPills.TestClipboard = "longer-tag-one, longer-tag-two, longer-tag-three";
            await Typed(full);
            await Tap(Key.V, ctrl: true);
            Check("tag pills: pasting a list makes pills: the tags the box hasn't got, as many whole ones as fit, one undo step",
                pasted == ("x, y, z, ", "x, y, z") && skipped == "co-op, Fast, " && unpasted == "co-op, " && box.Text == full + "longer-tag-one, longer-tag-two, ",
                $"{pasted}, \"{skipped}\", \"{unpasted}\", then {box.Text.Length} characters: ...{box.Text[Math.Max(0, box.Text.Length - 40)..]}");
            TagPills.TestClipboard = "Roguelike\vDeckbuilder, Co-op"; // \v: Word's line break (Shift+Enter)
            await Typed("");
            await Tap(Key.V, ctrl: true);
            Check("tag pills: a pasted list drops the characters below a space, as the box's own paste does", box.Text == "RoguelikeDeckbuilder, Co-op, ",
                Say().Replace("\v", "\\v"));
            TagPills.TestClipboard = "Co-op, Fast";
            await Typed("Co-op, Fast, Story");
            box.Select(13, 18); // "Story"
            await Tap(Key.V, ctrl: true);
            var overSelection = (box.Text, box.HasSelection());
            await Tap(Key.Z, ctrl: true);
            Check("tag pills: a paste over a selection with nothing new to add still takes the selection, one undo step",
                overSelection == ("Co-op, Fast, ", false) && box.Text == "Co-op, Fast, Story", $"{overSelection}, then \"{box.Text}\"");
            // After an emoji (two chars, one column), Paste splits the text and Copy reads the selection in the right place.
            TagPills.TestClipboard = "x, y";
            await Typed("🎮, A, ");
            await Tap(Key.V, ctrl: true);
            var pastedAfter = box.Text;
            await Typed("🎮, A, B");
            box.Select(3, 4); // "A"
            await Tap(Key.C, ctrl: true);
            Check("tag pills: after an emoji, a paste lands at the caret and Ctrl+C on a whole tag copies it with its comma",
                pastedAfter == "🎮, A, x, y, " && TagPills.TestClipboard == "A,", $"\"{pastedAfter}\", copied \"{TagPills.TestClipboard}\"");
            // A comma typed over a selected separator changes nothing, so it takes no undo step either.
            await Typed("A, B, wordx");
            await Tap(Key.Backspace); // the box's own: the word's last letter
            await Tap(Key.Z, ctrl: true);
            box.Select(1, 3); // ", "
            var sameAnnounced = 0;
            void SameAnnounced(string _) => sameAnnounced++;
            box.TextChanged += SameAnnounced;
            await Tap(Key.Comma, unicode: ',');
            box.TextChanged -= SameAnnounced;
            var same = box.Text;
            await Tap(Key.Y, ctrl: true);
            Check("tag pills: a comma typed over a selected separator changes nothing and takes no undo step: Ctrl+Y still redoes",
                same == "A, B, wordx" && sameAnnounced == 0 && box.Text == "A, B, word", $"\"{same}\", text_changed {sameAnnounced} times, then \"{box.Text}\"");
            // A comma the full box has no room for changes nothing, so it takes no undo step (one would clear the redo).
            var full200 = string.Join(", ", Enumerable.Range(1, 28).Select(i => $"tag{i:00}")) + ", word"; // the box's 200
            await Typed(full200);
            await Tap(Key.Backspace); // the box's own: the word's last letter
            await Tap(Key.Z, ctrl: true);
            var announced = 0;
            void Announced(string _) => announced++;
            box.TextChanged += Announced;
            await Tap(Key.Comma, unicode: ',');
            box.TextChanged -= Announced;
            var unchanged = box.Text == full200;
            await Tap(Key.Y, ctrl: true);
            Check("tag pills: a comma the full box has no room for changes nothing and takes no undo step: Ctrl+Y still redoes",
                full200.Length == 200 && unchanged && announced == 0 && box.Text == full200[..^1],
                $"{full200.Length} characters, unchanged {unchanged}, text_changed {announced} times, then ...{box.Text[Math.Max(0, box.Text.Length - 12)..]}");

            // A picked suggestion is one undo step (setting the text would have wiped the box's undo).
            await Typed("zebr");
            await Tap(Key.Down);
            var lit = TagSuggestions.Highlighted;
            await Tap(Key.Enter);
            var picked = box.Text;
            await Tap(Key.Z, ctrl: true);
            var unpicked = box.Text;
            await Tap(Key.Y, ctrl: true);
            Check("tag pills: Ctrl+Z takes back a picked suggestion, Ctrl+Y brings it back", lit == "Zebra-stripes" && picked == "Zebra-stripes, "
                && unpicked == "zebr" && box.Text == "Zebra-stripes, " && box.HasFocus(), $"lit {lit}: \"{picked}\", \"{unpicked}\", \"{box.Text}\"");

            // A long list: scrolled to the caret at its end, the first pills out of sight; Home brings them back.
            await Typed(string.Join(", ", Enumerable.Range(1, 20).Select(i => $"tag-{i:00}")) + ", wor");
            var atEnd = TagPills.TestPills(box);
            var scrolled = box.GetScrollOffset();
            await Capture(screen, outDir, "19d-tag-pills-scrolled");
            await Tap(Key.Home);
            var atStart = TagPills.TestPills(box);
            Check("tag pills: a long list scrolls, its pills with it, none drawn outside the box", scrolled < 0 && box.GetScrollOffset() == 0
                && atEnd.LastOrDefault().Tag == "tag-20" && atEnd.All(p => p.Tag != "tag-01") && atStart.FirstOrDefault().Tag == "tag-01" && atStart.All(p => p.Tag != "tag-20")
                && atEnd.Concat(atStart).All(p => inBox.Encloses(p.Rect)),
                $"scrolled {scrolled}: {string.Join(", ", atEnd.Select(p => $"{p.Tag} {p.Rect}"))}; at the start: {string.Join(", ", atStart.Select(p => $"{p.Tag} {p.Rect}"))} in {inBox}");

            await Typed("co-op, Favorites, , co-op, wor");
            await Tap(Key.Comma, unicode: ',');
            box.ReleaseFocus();
            await Wait(menu, 0.3);
            Check("tag pills: leaving the box still saves the tags tidied, its own text cream again", Store.InfoFor("SmmTestUi")?.Tags?.SequenceEqual(["co-op", "Favorites", "wor"]) == true
                && box.Text == "co-op, Favorites, wor" && box.GetThemeColor("font_color").IsEqualApprox(cream),
                $"\"{box.Text}\": {string.Join(", ", Store.InfoFor("SmmTestUi")?.Tags ?? [])}; {box.GetThemeColor("font_color")}");
        }
        finally
        {
            TagPills.TestClipboard = null;
            Store.RestoreEdits(before);
            CategoryList.Apply(content);
            ModDetails.Refresh(screen);
        }
        await Wait(menu, 0.3);
    }
}
