# DnW Mod Manager

A mod manager for **[Drag'n Wash](https://gatordragongames.itch.io/dragnwash)**, built for the
[DnW Mod Loader](https://github.com/KrazenLabs/dnw-modloader) (but also supports other mods!).

It allows you to install the mod loader and mods with one click, sets everything up automatically, checks your installation and can repair it if things are installed incorrectly. You can also install mods you downloaded from other sources and mod creators can create their own mod repositories that can be added for install and updates directly from the manager!

## Install

1. Download `DnWModManager.exe` from the most recent [release](https://github.com/KrazenLabs/dnw-modmanager/releases).
2. Run it. That's all.

It automatically finds the Drag'n Wash install (if installed via Steam of Itch.io App), but you can also manually set the install location.

## Installing mods

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
Provide `--game "<folder with DragNWash.exe>"` if the game is installed somewhere unusual.

## Mod repositories

The official KrazenLabs repository is provided by default, but you can also add repositories made by other creators.

## Building from source

Needs the .NET SDK 8 or 9.

```bash
pwsh ./build.ps1
```

Produces `release\DnWModManager-<version>.zip`

## Licence

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the libraries used.
