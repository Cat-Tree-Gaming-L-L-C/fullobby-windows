# HLL: Vietnam connect check

Thanks for helping! Starting Hell Let Loose: Vietnam with a server address the
way Fullobby normally does leaves the game on the main menu. This tries a few
other ways of starting it and records which one, if any, joins the server. It
takes about 10–15 minutes.

## What you need

- A Windows PC with **Hell Let Loose: Vietnam** installed through Steam
- The **server address** (IP:port) the Fullobby team gave you, and the server's
  **query port** if you were given one (the tests that need it are skipped otherwise)
- Fullobby **closed** (right-click its tray icon → Quit). The script will remind you.

## Steps

1. Unzip the folder (the script can't run from inside the zip).
2. Double-click **`Run-Check.cmd`**. If Windows shows "Windows protected your PC",
   click **More info → Run anyway**.
3. Enter the server address, and the query port if you have one.
4. The script runs up to four tests, each starting the game a different way.
   In **every** test, do the same thing:
   - As soon as the game window appears, press **Esc** every second or two for
     about 30 seconds (that's what Fullobby does).
   - If you're still on **"PRESS ANY BUTTON TO CONTINUE"** after that, press
     **Space** once.
   - Then wait about a minute **without touching anything**, Alt-Tab back to the
     script window and say where you ended up.
   - Quit the game to the desktop when the script asks, before the next test.
5. A file named `fullobby-hllv-check-<date>.zip` appears on your Desktop.
   **Send that file back to the Fullobby team.**

## What it does

- **Test A:** opens Steam's connect link, `steam://connect/<address>`.
- **Test B:** the same link with the query port (only if you gave one).
- **Test C:** `steam.exe -applaunch 3079210 +connect <ip>:<query port>` (only if
  you gave a query port).
- **Test D:** starts the game's launcher, `Launch_HLL.exe +connect <address>`,
  directly instead of through Steam.
- For each test it records when the game starts and the command lines the game
  and its launcher received, and saves your answer.
- At the end it lists the names of the files the game changed in its settings
  folder, and copies the game's logs if there are any (not crash reports or
  anti-cheat logs).

It doesn't install or change anything.

## What's in the file you send

- Your answers and any notes you typed
- Your Windows version, where Steam and the game are installed, the server
  address and query port
- The command lines passed to the game and its launcher
- The names of game folders in `%LOCALAPPDATA%` that have Unreal Engine save
  data that changed during the test, and the names and sizes (not contents) of
  the files the game changed in `%LOCALAPPDATA%\HLLVietnam\Saved`
- The game's logs from the test, if any, which include your **in-game name**

Before anything is saved, the script replaces your **Windows user name**, **PC
name**, **user folder path** and **Steam ID** with placeholders such as `<user>`.
You can open the zip and check it before sending it.

If something goes wrong partway through, the script says so and still makes the
zip, so please send it anyway.
