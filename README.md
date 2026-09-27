![DnW Mod Manager](https://github.com/KrazenLabs/dnw-modmanager/blob/main/assets/logo.png)

# DnW Mod Manager

https://github.com/KrazenLabs/dnw-modmanager/blob/main/assets/logo.png

A mod manager for **[Drag'n Wash](https://gatordragongames.itch.io/dragnwash)**, built for the
[DnW Mod Loader](https://github.com/KrazenLabs/dnw-modloader) (but also supports other mods!).

It allows you to install the mod loader and mods with one click, sets everything up automatically, checks your installation and can repair it if things are installed incorrectly. You can also install mods you downloaded from other sources and mod creators can create their own mod repositories that can be added for install and updates directly from the manager!

![Main Screen](https://github.com/KrazenLabs/dnw-modmanager/blob/main/assets/main_screen.png)

![Mod Installation](https://github.com/KrazenLabs/dnw-modmanager/blob/main/assets/mod_install.png)

## Install

### Windows

1. Download `DnWModManager.exe` from the most recent [release](https://github.com/KrazenLabs/dnw-modmanager/releases).
2. Run it. That's all.

### Linux

1. Download `DnWModManager-linux-x64.zip` from the most recent [release](https://github.com/KrazenLabs/dnw-modmanager/releases) and extract it.
2. Run `DnWModManager` (if your file manager does not start it, run `chmod +x DnWModManager` first).
   To start it from your application menu from then on, click "Add to menu" under Settings (on the Steam Deck it is then listed under Games in Desktop Mode).
3. Install the mod loader from the manager, then set the Steam launch option the manager shows under Settings
   (right-click the game in Steam, Properties, General, Launch Options):
   - native Linux version (Steam's default): `./run_dnw.sh %command%`
   - Windows version through Proton: `WINEDLLOVERRIDES="winhttp=n,b" %command%`

The manager installs the right loader files for the version of the game it finds. It needs a desktop session (X11, or Wayland with XWayland).
"Install from zip..." and the other file choosers use your desktop's file dialog (through xdg-desktop-portal, or GTK 3 when there is no portal); if neither is available, a simpler built-in file dialog opens instead.

It automatically finds the Drag'n Wash install (if installed via Steam or the itch.io app; on Linux also the Flatpak and Snap versions of Steam), but you can also manually set the install location.

## Installing and updating mods

Everything should be fairly self-explanatory within the application!

## Command line

If your installation is broken in a way that prevents the mod manager window from working, you can use the command line to create a report or to run the automatic fixes:

```bash
DnWModManager.exe --report
```
Provides a debugging report.

```bash
DnWModManager.exe --fix
```
Applies every fix the report listed.

`--install "<zip or folder>"` and `--uninstall <mod id>` let you install and uninstall mods via CLI.
Provide `--game "<game folder>"` if the game is installed somewhere unusual. `--version` prints the manager's version.
On Linux, use `./DnWModManager` instead of `DnWModManager.exe`.

## Mod repositories

The official KrazenLabs repository is provided by default, but you can also add repositories made by other creators.

## Building from source

Needs the .NET SDK 8 or 9. The UI uses [Avalonia](https://avaloniaui.net), so the same code builds for Windows and Linux.

```bash
pwsh ./build.ps1
```

Produces `release\DnWModManager.exe` (Windows) and `release\DnWModManager-linux-x64.zip` (Linux).

## Licence

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the libraries used.
