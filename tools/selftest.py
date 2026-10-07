"""Build, install and run the in-game self-test (src/SpireModManager/SelfTest.cs), unattended.

Backs up the account's settings.save and our mod_data folder, launches the game with
SPIRE_MOD_MANAGER_SELFTEST set, waits for it to quit, restores the backups, then prints
out/selftest/results.txt and any of our log lines. Screenshots land in out/selftest/*.png.
The game must be closed first; Steam must be running.
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME_DIR = r'D:\SteamLibrary\steamapps\common\Slay the Spire 2'
EXE = os.path.join(GAME_DIR, 'SlayTheSpire2.exe')
APP_ID = '2868840'
USER_DATA = os.path.join(os.environ['APPDATA'], 'SlayTheSpire2')
OUT = os.path.join(ROOT, 'out', 'selftest')
TIMEOUT = 180


def game_running():
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq SlayTheSpire2.exe'], capture_output=True, text=True).stdout
    return 'SlayTheSpire2.exe' in out


def main():
    if game_running():
        sys.exit('Close the game first.')
    subprocess.run([sys.executable, os.path.join(ROOT, 'build.py')], check=True)

    accounts = glob.glob(os.path.join(USER_DATA, 'steam', '*', 'settings.save'))
    if len(accounts) != 1:
        sys.exit(f'Expected one account settings.save, found {accounts}')
    settings = accounts[0]
    mod_data = os.path.join(os.path.dirname(settings), 'mod_data', 'SpireModManager')

    backup = tempfile.mkdtemp(prefix='smm-selftest-')
    shutil.copyfile(settings, os.path.join(backup, 'settings.save'))
    had_mod_data = os.path.isdir(mod_data)
    if had_mod_data:
        shutil.copytree(mod_data, os.path.join(backup, 'mod_data'))
    print('backed up to', backup)

    shutil.rmtree(OUT, ignore_errors=True)
    os.makedirs(OUT)
    log = os.path.join(USER_DATA, 'logs', 'godot.log')
    env = dict(os.environ, SteamAppId=APP_ID, SteamGameId=APP_ID, SPIRE_MOD_MANAGER_SELFTEST=OUT)
    try:
        proc = subprocess.Popen([EXE], cwd=GAME_DIR, env=env)
        try:
            proc.wait(TIMEOUT)
        except subprocess.TimeoutExpired:
            print(f'game still running after {TIMEOUT}s, killing it')
            proc.kill()
            proc.wait()
        time.sleep(2)  # let Steam and the log settle
    finally:
        # copyfile, not copy2: a fresh timestamp so Steam Cloud treats the restored file as newest.
        shutil.copyfile(os.path.join(backup, 'settings.save'), settings)
        shutil.rmtree(mod_data, ignore_errors=True)
        if had_mod_data:
            shutil.copytree(os.path.join(backup, 'mod_data'), mod_data)
        print('restored settings and mod_data')

    results = os.path.join(OUT, 'results.txt')
    print('\n'.join(open(results, encoding='utf-8').read().splitlines()) if os.path.exists(results) else 'NO RESULTS (test did not finish)')
    print('--- log ---')
    for line in open(log, encoding='utf-8', errors='replace'):
        if 'SpireModManager' in line or ('[ERROR]' in line and 'Spire' in line):
            print(line.rstrip()[:400])
    print('screenshots:', ', '.join(sorted(f for f in os.listdir(OUT) if f.endswith('.png'))))
    if not os.path.exists(results) or 'FAIL' in open(results, encoding='utf-8').read():
        sys.exit(1)


if __name__ == '__main__':
    main()
