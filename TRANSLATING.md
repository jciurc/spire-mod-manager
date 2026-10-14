# Translating Spire Mod Manager

All text the player sees lives in `localization/<language>/`. English (`eng`) is the source.

## Add a language

1. Make a folder named with the game's three-letter code (table below), e.g. `localization/deu/`.
2. Copy the JSON files from `localization/eng/` into it and translate the **values** only:

   ```json
   { "SPIRE_MOD_MANAGER.RELAUNCH_BUTTON": "Spiel neu starten" }
   ```

3. Partial translations are fine: any key you leave out shows in English.

Rules:

- Translate it yourself: no machine or AI translation in a translation packed into the mod. (Drop-in files, below,
  are not reviewed and may come from anywhere.)
- Never change the keys (left side). The build rejects keys that don't exist in English.
- Keep placeholders such as `{count}` and tags such as `[gold]...[/gold]` exactly as they are. You can move a
  placeholder to wherever your language needs it: `"Changes {count}"` can become `"{count} Änderungen"`.
- The file names (`settings_ui.json`, ...) are the game's own text tables; keep them.

## Use the game's words

If the game is translated into your language, use its words: where our text names something the game shows too,
use the same word, so players see one word for one thing. In Simplified Chinese, for example, mods are 模组, as on
the game's own Modding screen. For Steam's things, use Steam's word: the Workshop is 创意工坊.

To look a word up, switch the game to your language and find it in its menus. Or read the game's text tables:
they are in `SlayTheSpire2.pck`, under `localization/<code>/`, and `tools/pck.cs` in this repo lists and
extracts them:

```
dotnet run tools/pck.cs list "<game folder>/SlayTheSpire2.pck" localization/zhs
dotnet run tools/pck.cs extract "<game folder>/SlayTheSpire2.pck" localization/zhs/settings_ui.json game_settings_ui.json
```

## Dates and numbers

Dates and numbers follow the language the game is set to, with nothing to translate: dates use the game's own
date format for that language (the one its run history shows), and numbers its decimal and thousands separators
("1,5 MB" in German). Units are ordinary text, e.g. `"{size} MB"` can become `"{size} Mo"` in French.

## Fonts

Nothing to do here either. Our text uses the font the game uses for its language: Kreon in most, and the game's
own fonts in Chinese, Japanese, Korean, Thai, Russian and Polish.

## Drop-in translation files

The mod also reads a translation from one file, with no build: the quickest way to try yours in the game, and how a
translation kept outside this repository is installed. Drop-in files may come from anywhere, AI-made ones included,
such as those the author hosts outside this repository; they are not reviewed.

1. On the main menu, right-click the Spire Mod Manager button and choose **Open translations folder**.
2. Put the file there, named with the game's language code (table below): `zhs.json` for Simplified Chinese. It holds
   the same as `localization/eng/settings_ui.json`, with the values translated. All our text is in that one file.
   Only the languages in the game's own language setting work.
3. Switch the game to that language. If it's in that language already, relaunch it.

The file comes first, key by key: a key it leaves out shows as the translation built into the mod, or in English.
Keys English doesn't have, empty texts and texts with a broken or renamed placeholder are left out, and a file that
isn't valid JSON or is over 1 MB is ignored; the game's log (`godot.log`) says which, and lists the folder's files if
none has the language's name. English is the source, so there's no `eng.json`. The folder is `%APPDATA%\SlayTheSpire2\steam\<your Steam id>\mod_data\SpireModManager\translations`.

## Test it in the game

1. Put your file in the translations folder (above), build the mod with your folder in it (CONTRIBUTING.md,
   "Building"), or ask for a test build in a translation issue.
2. Switch the game to your language in its settings. If some of our text stays in the old language, relaunch the
   game.
3. Open the Modding screen and go through every part: the main list, Vanilla List, Load Order, the info panel,
   the profile bar, Changes, the right-click menus, the main menu button, and the popups (a new mod, the
   relaunch check, deleting a profile or a category).
4. Look for text that is cut off or runs past its box (many labels have little room), English you meant to
   translate, and placeholders that show as `{count}` instead of a number.

## Language codes

The languages the game offers. Our text can only show in one of these.

| Code | Language | Code | Language |
| --- | --- | --- | --- |
| `deu` | German | `kor` | Korean |
| `eng` | English | `pol` | Polish |
| `esp` | Spanish (Latin America) | `ptb` | Portuguese (Brazil) |
| `fra` | French | `rus` | Russian |
| `ind` | Indonesian | `spa` | Spanish (Spain) |
| `ita` | Italian | `tha` | Thai |
| `jpn` | Japanese | `tur` | Turkish |
| `zhs` | Chinese (Simplified) | `zht` | Chinese (Traditional) |

## Sending a translation

Open a pull request with your folder at https://github.com/jciurc/spire-mod-manager, or send the files to the author. Before a language ships, a second native speaker checks it in the game. Credit goes in the Workshop description.
