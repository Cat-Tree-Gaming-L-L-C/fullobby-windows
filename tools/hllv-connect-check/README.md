# HLL: Vietnam connect check

Thanks for helping! This checks whether Hell Let Loose: Vietnam joins a server
when Steam starts it with a server address, which is how Fullobby launches the
game for seeding. It takes about 5–10 minutes.

## What you need

- A Windows PC with **Hell Let Loose: Vietnam** installed through Steam
- The **server address** (IP:port) the Fullobby team gave you
- Fullobby **closed** (right-click its tray icon → Quit). The script will remind you.

## Steps

1. Put `Run-Check.cmd` and `Check-HllvConnect.ps1` in the same folder.
2. Double-click **`Run-Check.cmd`**. If Windows shows "Windows protected your PC",
   click **More info → Run anyway**.
3. Enter the server address when asked.
4. **Test 1:** when the game opens, **don't touch anything**. Let the intro
   videos play and wait about 2 minutes. Then Alt-Tab back to the script
   window and say where you ended up.
5. **Test 2** runs only if Test 1 joined the server. This time, press **Esc**
   a few times as soon as the game window appears to skip the intros.
6. Quit the game when the script asks, so the game's log gets saved.
7. A file named `fullobby-hllv-check-<date>.zip` appears on your Desktop.
   **Send that file back to the Fullobby team.**

## What it does

- It launches the game with `steam.exe -applaunch 3079210 +connect <address>`.
- It records when the game starts and the command line the game received.
- It saves your answers and a copy of the game's log from the test.

It doesn't install or change anything. The game log includes your in-game
name; nothing else personal is collected.
