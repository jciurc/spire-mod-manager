"""Build SpireModManager (pack + DLL + manifest) and install it into the game's mods folder."""
import argparse
import json
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import pck  # noqa: E402

MOD_ID = 'SpireModManager'
STEAM = r'D:\SteamLibrary\steamapps'
INSTALL_DIR = os.path.join(STEAM, 'common', 'Slay the Spire 2', 'mods', MOD_ID)
PROJECT = os.path.join(ROOT, 'src', MOD_ID, MOD_ID + '.csproj')
OUT = os.path.join(ROOT, 'out')

MANIFEST = {
    'id': MOD_ID,
    'pck_name': MOD_ID,
    'name': 'Spire Mod Manager',
    'author': 'Jordan',
    'description': 'Mod profiles, sorting and an in-place Restart for the Modding screen.',
    'version': '0.1.0',
    'has_pck': True,
    'has_dll': True,
    'affects_gameplay': False,
    'dependencies': [],
    'min_game_version': '0.111.0',
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--no-install', action='store_true')
    args = parser.parse_args()

    shutil.rmtree(OUT, ignore_errors=True)
    os.makedirs(OUT)

    # Localization tables: localization/<lang>/<table>.json -> res://SpireModManager/localization/...
    # Translations only need the keys they translate (missing keys fall back to eng), but every key
    # must exist in eng, so a typo fails the build instead of showing nothing.
    files = {}
    loc_root = os.path.join(ROOT, 'localization')
    eng = {name: json.load(open(os.path.join(loc_root, 'eng', name), encoding='utf-8'))
           for name in os.listdir(os.path.join(loc_root, 'eng'))}
    for lang in os.listdir(loc_root):
        for name in os.listdir(os.path.join(loc_root, lang)):
            blob = open(os.path.join(loc_root, lang, name), 'rb').read()
            unknown = set(json.loads(blob)) - set(eng.get(name, {}))  # also fails on malformed JSON
            if unknown:
                sys.exit(f'localization/{lang}/{name}: keys not in eng: {sorted(unknown)}')
            files[f'{MOD_ID}/localization/{lang}/{name}'] = blob
    pck.write(os.path.join(OUT, MOD_ID + '.pck'), files.items())
    json.dump(MANIFEST, open(os.path.join(OUT, MOD_ID + '.json'), 'w', encoding='utf-8'), indent=2)

    subprocess.run(['dotnet', 'build', PROJECT, '-c', 'Release', '-nologo', '-v', 'q',
                    f'-p:SteamLibrary={STEAM}', f'-o:{os.path.join(OUT, "bin")}'], check=True)
    shutil.copy(os.path.join(OUT, 'bin', MOD_ID + '.dll'), OUT)
    print(f'built {len(files)} pack file(s)')

    if not args.no_install:
        os.makedirs(INSTALL_DIR, exist_ok=True)
        for ext in ('.pck', '.json', '.dll'):
            shutil.copy(os.path.join(OUT, MOD_ID + ext), INSTALL_DIR)
        print('installed to', INSTALL_DIR)


if __name__ == '__main__':
    main()
