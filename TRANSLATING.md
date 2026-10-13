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

- Never change the keys (left side). The build rejects keys that don't exist in English.
- Keep placeholders such as `{count}` and tags such as `[gold]...[/gold]` exactly as they are.
- The file names (`settings_ui.json`, ...) are the game's own text tables; keep them.

## Language codes

| Code | Language | Code | Language |
| --- | --- | --- | --- |
| `ara` | Arabic | `kor` | Korean |
| `cze` | Czech | `nor` | Norwegian |
| `deu` | German | `pol` | Polish |
| `dut` | Dutch | `por` | Portuguese |
| `eng` | English | `ptb` | Portuguese (Brazil) |
| `esp` | Spanish (Latin America) | `rus` | Russian |
| `fra` | French | `spa` | Spanish (Spain) |
| `gre` | Greek | `swe` | Swedish |
| `ind` | Indonesian | `tha` | Thai |
| `ita` | Italian | `tur` | Turkish |
| `jpn` | Japanese | `ukr` | Ukrainian |
| `vie` | Vietnamese | `zhs` / `zht` | Chinese (Simplified / Traditional) |

## Sending a translation

Open a pull request with your folder at https://github.com/jciurc/spire-mod-manager, or send the files to the author. Credit goes in the Workshop description.
