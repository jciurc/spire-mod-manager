using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireModManager;

// The note while mods load slowly (LoadingNote): this launch's, if it showed, gone with the game's first frame; drawn
// again by hand, each step turning the spinner, forced onto the screen mid-frame (a windowed run's picture: loading-note),
// then gone at the next frame.
static partial class SelfTest
{
    static async Task TestLoadingNote(NMainMenu menu, string outDir)
    {
        Check("the loading note: if it showed at this launch (mods slow to load, or SPIRE_MOD_MANAGER_LOADING_AFTER), it went at the game's first frame",
            !LoadingNote.Shown || LoadingNote.Gone, $"shown {LoadingNote.Shown}, gone {LoadingNote.Gone}");
        LoadingNote.TestReset();
        var draws = LoadingNote.Draws;
        LoadingNote.Draw(214);
        LoadingNote.Draw(214);
        var picture = Path.Combine(outDir, "loading-note.png");
        if (DisplayServer.GetName() != "headless")
            menu.GetViewport().GetTexture().GetImage().SavePng(picture); // what ForceDraw put on the screen
        var drawn = LoadingNote.Draws - draws;
        await Wait(menu, 0.2);
        Check("the loading note: drawn and forced onto the screen mid-frame, twice; gone at the next frame", drawn == 2 && LoadingNote.Gone,
            $"drawn {drawn}, gone {LoadingNote.Gone}");
    }
}
