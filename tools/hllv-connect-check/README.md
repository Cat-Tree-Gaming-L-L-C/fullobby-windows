# HLL: Vietnam connect check

Thanks for helping! This checks whether Hell Let Loose: Vietnam joins a server
when Steam starts it with a server address, which is how Fullobby launches the
game for seeding. It takes about 5–10 minutes.

## What you need

- A Windows PC with **Hell Let Loose: Vietnam** installed through Steam
- The **server address** (IP:port) the Fullobby team gave you
- Fullobby **closed** (right-click its tray icon → Quit). The script will remind you.

## Steps

1. Unzip the folder (the script can't run from inside the zip).
2. Double-click **`Run-Check.cmd`**. If Windows shows "Windows protected your PC",
   click **More info → Run anyway**.
3. Enter the server address when asked.
4. **Test 1:** when the game opens, **don't touch anything**. Let the intro
   videos play and wait about 2 minutes. Then Alt-Tab back to the script
   window and say where you ended up. Being stuck on **"PRESS ANY BUTTON TO
   CONTINUE"** is a normal answer; there's an option for it.
5. **Test 2:** as soon as the game window appears, press **Esc** every second
   or two for about 30 seconds (that's what Fullobby does). Use only Esc at
   first; if you're still on "PRESS ANY BUTTON TO CONTINUE", press **Space**
   once. Then wait a minute without touching anything and say where you
   ended up.
6. Quit the game when the script asks, so the game's log gets saved.
7. A file named `fullobby-hllv-check-<date>.zip` appears on your Desktop.
   **Send that file back to the Fullobby team.**

## What it does

- It launches the game with `steam.exe -applaunch 3079210 +connect <address>`.
- It records when the game starts and the command line the game received.
- It saves your answers and a copy of the game's logs from the test (not crash
  reports or anti-cheat logs).

It doesn't install or change anything.

## What's in the file you send

- Your answers and any notes you typed
- Your Windows version, where Steam and the game are installed, and the server
  address
- The command lines Steam passed to the game and its launcher
- The names of game folders in `%LOCALAPPDATA%` that have Unreal Engine save
  data that changed during the test (used to find the game's log)
- The game's logs from the test, which include your **in-game name**

Before anything is saved, the script replaces your **Windows user name**, **PC
name**, **user folder path** and **Steam ID** with placeholders such as `<user>`.
You can open the zip and check it before sending it.

If something goes wrong partway through, the script says so and still makes the
zip, so please send it anyway.
