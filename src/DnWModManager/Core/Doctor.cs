namespace DnWModManager.Core;

public static class Doctor
{
    public static void Diagnose(ScanResult scan, ModCatalog catalog = null)
    {
        scan.Diagnostics.Clear();

        CheckLoader(scan);
        CheckRivalLoaders(scan);
        CheckCoreLibraries(scan);
        CheckStrays(scan);
        CheckMods(scan, catalog);
        CheckDuplicateIds(scan);

        var ordered = scan.Diagnostics
            .OrderByDescending(d => d.Severity)
            .ToList();
        scan.Diagnostics.Clear();
        scan.Diagnostics.AddRange(ordered);
    }

    private static void CheckLoader(ScanResult scan)
    {
        var install = scan.Install;
        var loader = scan.Loader;

        if (!loader.AssemblyPresent && !loader.DoorstopProxyPresent)
        {
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "loader.missing",
                Severity = Severity.Error,
                Title = "The DnW Mod Loader is not installed",
                Detail = "The DnW Mod Loader is required to load mods.",
                Path = install.GameDirectory,
                Repair = new Repair
                {
                    Label = "Install the Mod Loader",
                    Description = "Downloads and installs the latest DnW Mod Loader release.",
                    NeedsNetwork = true,
                    Apply = InstallLatestLoaderAsync,
                },
            });
            return;
        }

        if (!loader.DoorstopProxyPresent)
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "loader.proxy.missing",
                Severity = Severity.Error,
                Title = "winhttp.dll is missing",
                Detail = "The Mod Loader installation is incomplete.",
                Path = install.DoorstopProxyPath,
                Repair = new Repair
                {
                    Label = "Reinstall the Mod Loader",
                    Description = "Downloads and reinstalls the latest DnW Mod Loader release.",
                    NeedsNetwork = true,
                    Apply = InstallLatestLoaderAsync,
                },
            });

        if (!loader.AssemblyPresent)
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "loader.runtime.missing",
                Severity = Severity.Error,
                Title = "The loader runtime is missing",
                Detail = "The Mod Loader installation is incomplete.",
                Path = install.LoaderDirectory,
                Repair = new Repair
                {
                    Label = "Reinstall the loader",
                    Description = "Downloads and reinstalls the latest DnW Mod Loader release.",
                    NeedsNetwork = true,
                    Apply = InstallLatestLoaderAsync,
                },
            });

        if (loader.MissingRuntimeFiles.Count > 0)
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "loader.runtime.incomplete",
                Severity = Severity.Error,
                Title = "The loader install is incomplete",
                Detail = "The Mod Loader installation is incomplete. (Missing files: "
                         + string.Join(", ", loader.MissingRuntimeFiles)
                         + ").",
                Path = install.LoaderDirectory,
                Repair = new Repair
                {
                    Label = "Reinstall the loader",
                    Description = "Downloads and reinstalls the latest DnW Mod Loader release.",
                    NeedsNetwork = true,
                    Apply = InstallLatestLoaderAsync,
                },
            });

        CheckDoorstop(scan);
    }

    private static void CheckDoorstop(ScanResult scan)
    {
        var install = scan.Install;
        var doorstop = scan.Loader.Doorstop;

        if (!scan.Loader.DoorstopConfigPresent || doorstop is null)
        {
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "doorstop.config.missing",
                Severity = Severity.Error,
                Title = "doorstop_config.ini is missing",
                Detail = "The DnW Mod Loader configuration file is missing.",
                Path = install.DoorstopConfigPath,
                Repair = new Repair
                {
                    Label = "Repair",
                    Description = "Writes the correct Mod Loader configuration.",
                    Apply = (context, _) =>
                    {
                        DoorstopConfig.WriteDefault(context.Install.DoorstopConfigPath);
                        return Task.FromResult("Created " + context.Install.Relative(context.Install.DoorstopConfigPath) + ".");
                    },
                },
            });
            return;
        }

        if (!doorstop.Enabled)
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "doorstop.disabled",
                Severity = Severity.Error,
                Title = "Mod loading is disabled in doorstop_config.ini",
                Detail = "The Mod Loader is currently disabled.",
                Path = install.DoorstopConfigPath,
                Repair = new Repair
                {
                    Label = "Enable Mod Loader",
                    Description = "Re-enables the Loader in the configuration file.",
                    Apply = (context, _) => Task.FromResult(RepairDoorstop(context, "Enabled mod loading.")),
                },
            });

        string foreign = doorstop.ForeignTargetName;
        if (foreign is not null)
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "doorstop.foreign",
                Severity = Severity.Error,
                Title = "doorstop_config.ini starts " + foreign + " instead of DnW Mod Loader",
                Detail = "The Mod Loader configuration was overwritten by a different loader.",
                Path = install.DoorstopConfigPath,
                Repair = new Repair
                {
                    Label = "Repair",
                    Description = "Fixes the configuration to start the DnW Mod Loader.",
                    Apply = (context, _) => Task.FromResult(RepairDoorstop(context, "doorstop_config.ini now starts DnW Mod Loader.")),
                },
            });
    }

    private static string RepairDoorstop(RepairContext context, string message)
    {
        var doorstop = DoorstopConfig.Load(context.Install.DoorstopConfigPath)
                       ?? throw new IOException("doorstop_config.ini could not be read.");
        doorstop.RepairForLoader();
        doorstop.Save();
        return message;
    }

    private static async Task<string> InstallLatestLoaderAsync(RepairContext context, CancellationToken cancel)
    {
        var packages = context.Packages
                       ?? throw new InvalidOperationException("No network connection.");

        var source = new ModSource { Type = "github", Repo = "KrazenLabs/dnw-modloader" };
        var release = await packages.LatestReleaseAsync(source, cancel).ConfigureAwait(false)
                      ?? throw new IOException("Could not reach GitHub to find the latest loader release.");

        if (string.IsNullOrEmpty(release.DownloadUrl))
            throw new IOException("The latest loader release (" + release.Version + ") has no downloadable file.");

        bool isUpdate = File.Exists(context.Install.LoaderAssemblyPath);

        if (isUpdate)
        {
            string installed = ModScanner.ReadAssemblyVersion(context.Install.LoaderAssemblyPath);
            if (installed is not null && ModVersion.Compare(release.Version, installed) < 0)
                throw new InvalidOperationException(
                    "The newest published loader is " + release.Version + ", but " + installed
                    + " is already installed.");
        }

        string zip = await packages.DownloadAsync(release.DownloadUrl, release.AssetName, null, cancel).ConfigureAwait(false);
        using var staged = packages.Stage(zip, context.Install);

        LoaderInstaller.Install(staged, context.Install, context.Quarantine, isUpdate);

        return (isUpdate ? "Updated DnW Mod Loader to version " : "Installed DnW Mod Loader version ") + release.Version + ".";
    }

    private static void CheckRivalLoaders(ScanResult scan)
    {
        foreach (var rival in scan.Rivals)
        {
            string path = rival.Path;
            bool isProxy = rival.IsProxy;

            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "rival." + rival.Name.ToLowerInvariant(),
                Severity = Severity.Error,
                Title = rival.Name + " is installed",
                Detail = "Found another mod loader: " + rival.Evidence + ". Multiple mod loaders are not compatible with each other."
                        + "Note that the DnW Mod Loader supports BepInEx and MelonLoader mods as well, so there is no need to install these separately.",
                Path = path,
                Repair = new Repair
                {
                    Label = "Quarantine " + rival.Name,
                    Description = "Quarantines " + rival.Name + " (installed mod or plugins remain intact).",
                    IsDestructive = true,
                    Apply = (context, _) => Task.FromResult(RemoveRival(context, rival, isProxy)),
                },
            });
        }
    }

    private static string RemoveRival(RepairContext context, RivalLoader rival, bool isProxy)
    {
        var install = context.Install;
        var moved = new List<string>();

        if (rival.Name == "MelonLoader")
        {
            foreach (var name in new[] { "version.dll", "dobby.dll", "NOTICE.txt" })
            {
                string path = Path.Combine(install.GameDirectory, name);
                if (!File.Exists(path)) continue;
                context.Quarantine.TakeFile(path);
                moved.Add(name);
            }

            if (Directory.Exists(install.MelonLoaderDirectory))
            {
                context.Quarantine.TakeDirectory(install.MelonLoaderDirectory);
                moved.Add(install.Relative(install.MelonLoaderDirectory));
            }
        }
        else if (rival.Name == "BepInEx")
        {
            string core = install.BepInExCoreDirectory;
            if (Directory.Exists(core))
            {
                var entries = Directory.GetFileSystemEntries(core);
                var own = entries.Where(e => File.Exists(e) && InstallPlanner.IsBepInExCoreFile(Path.GetFileName(e))).ToList();
                if (own.Count == entries.Length)
                {
                    context.Quarantine.TakeDirectory(core);
                    moved.Add(install.Relative(core));
                }
                else if (own.Count > 0)
                {
                    foreach (var file in own) context.Quarantine.TakeFile(file);
                    moved.Add("BepInEx files in " + install.Relative(core));
                }
            }

            if (Directory.Exists(install.BepInExPatchersDirectory))
            {
                context.Quarantine.TakeDirectory(install.BepInExPatchersDirectory);
                moved.Add(install.Relative(install.BepInExPatchersDirectory));
            }

            var doorstop = DoorstopConfig.Load(install.DoorstopConfigPath);
            if (doorstop is not null && doorstop.RepairForLoader())
            {
                doorstop.Save();
                moved.Add("doorstop_config.ini (repointed at the loader)");
            }
        }
        else if (Directory.Exists(rival.Path))
        {
            context.Quarantine.TakeDirectory(rival.Path);
            moved.Add(install.Relative(rival.Path));
        }

        return moved.Count == 0
            ? "Done moving files."
            : "Moved: " + string.Join(", ", moved) + ".";
    }

    private static void CheckCoreLibraries(ScanResult scan)
    {
        var install = scan.Install;
        foreach (var path in scan.CoreLibraries)
        {
            string name = Path.GetFileName(path);
            string target = InstallPlanner.PluginLibraryTarget(name);
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "bepinex.core.library",
                Severity = Severity.Warning,
                Title = name + " is in BepInEx\\core",
                Detail = name + " is installed in the wrong folder.",
                Path = path,
                Repair = new Repair
                {
                    Label = "Repair",
                    Description = "Move " + install.Relative(path) + " to " + target + ".",
                    Apply = (context, _) => Task.FromResult(MoveCoreLibrary(context, path)),
                },
            });
        }
    }

    private static string MoveCoreLibrary(RepairContext context, string path)
    {
        if (!File.Exists(path)) return "Already moved.";

        var install = context.Install;
        string name = Path.GetFileName(path);
        string target = Path.Combine(install.GameDirectory, InstallPlanner.PluginLibraryTarget(name));
        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);

        var files = Directory.GetFiles(directory, stem + ".*")
            .Where(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool alreadyThere = File.Exists(target);
        string plugins = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(plugins);

        foreach (var file in files)
        {
            string destination = Path.Combine(plugins, Path.GetFileName(file));
            if (alreadyThere || File.Exists(destination)) context.Quarantine.TakeFile(file);
            else File.Move(file, destination);
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);

        return alreadyThere
            ? name + " already exists in BepInEx\\plugins."
            : "Moved " + name + " to BepInEx\\plugins.";
    }

    private static void CheckStrays(ScanResult scan)
    {
        foreach (var stray in scan.Strays.Where(s => s.Kind == ModKind.LoaderRuntime))
        {
            string path = stray.Path;
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "stray.runtime",
                Severity = Severity.Error,
                Title = stray.FileName + " is in the wrong location",
                Detail = "This is a misplaced mod loader file.",
                Path = path,
                Repair = new Repair
                {
                    Label = "Quarantine",
                    Description = "Quarantine " + scan.Install.Relative(path),
                    Apply = (context, _) =>
                    {
                        if (!File.Exists(path)) return Task.FromResult("Already removed.");
                        context.Quarantine.TakeFile(path);
                        return Task.FromResult("Quarantined " + context.Install.Relative(path) + ".");
                    },
                },
            });
        }

        foreach (var stray in scan.Strays.Where(s => s.Kind != ModKind.LoaderRuntime))
            scan.Diagnostics.Add(new Diagnostic
            {
                Code = "stray.assembly",
                Severity = Severity.Warning,
                Title = stray.FileName + " is in the wrong location",
                Detail = stray.Kind.Label() + " is in the wrong folder.",
                Path = stray.Path,
            });
    }

    private static void CheckMods(ScanResult scan, ModCatalog catalog)
    {
        foreach (var mod in scan.Mods)
        {
            CheckModManifest(scan, mod);
            CheckModLocation(scan, mod);
            CheckModSupport(scan, mod);
            CheckModCompanions(scan, mod);
            CheckModReferences(scan, mod);
            CheckModLoaderVersion(scan, mod);
        }

        CheckDependencies(scan, catalog);
    }

    private static void CheckModManifest(ScanResult scan, InstalledMod mod)
    {
        var manifest = mod.Manifest;
        if (manifest is null) return;

        if (manifest.ParseError is not null)
        {
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.manifest.invalid",
                Severity = Severity.Error,
                Title = mod.Name + ": mod.json cannot be read",
                Detail = "A mod configuration could not be read: " + manifest.ParseError,
                Path = manifest.Path,
                Mod = mod,
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.manifest.noid",
                Severity = Severity.Error,
                Title = mod.Name + ": mod.json has no id",
                Detail = "Mod ID is missing.",
                Path = manifest.Path,
                Mod = mod,
            });
        else if (!ModManifestFile.IsValidId(manifest.Id))
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.manifest.badid",
                Severity = Severity.Error,
                Title = mod.Name + ": invalid mod id: \"" + manifest.Id + "\"",
                Detail = "Ids must be lower-case and may only contain letters, digits, dots, underscores "
                         + "and hyphens.",
                Path = manifest.Path,
                Mod = mod,
            });

        if (mod.Kind == ModKind.Unknown && mod.Probe?.ReadError is not null)
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.nodll",
                Severity = Severity.Warning,
                Title = mod.Name + ": " + mod.Probe.ReadError,
                Detail = "Found a mod configuration but no mod DLL.",
                Path = mod.Directory,
                Mod = mod,
            });
    }

    private static void CheckModLocation(ScanResult scan, InstalledMod mod)
    {
        if (!mod.Kind.IsRunnable() || mod.LocationWorks) return;

        bool shadowedByManifest = mod.Manifest is not null
                                  && mod.Kind is ModKind.BepInExPlugin or ModKind.MelonMod
                                  && mod.Location is ModLocation.ModsSubfolder or ModLocation.ModsRoot;

        string detail = shadowedByManifest
            ? "Mod of type " + mod.Kind.Label() + " is installed in the wrong location."
            : mod.Location switch
            {
                ModLocation.GameRoot =>
                    "(Installed in game folder)",
                ModLocation.TooDeep =>
                    "(Nested install)",
                ModLocation.BepInExPatchers =>
                    "(BepInEx\\patchers)",
                ModLocation.MelonPlugins =>
                    "(MelonLoader Plugins folder)",
                _ => "(outside loader structure)",
            };

        string target = mod.CanonicalDirectory(scan.Install);
        if (target is null) return;

        Add(scan, mod, new Diagnostic
        {
            Code = "mod.location",
            Severity = Severity.Error,
            Title = mod.Name + " is installed in the wrong location",
            Detail = detail,
            Path = mod.AssemblyPath,
            Mod = mod,
            Repair = new Repair
            {
                Label = "Move to " + scan.Install.Relative(target),
                Description = "Move " + Path.GetFileName(mod.AssemblyPath) + " to " + scan.Install.Relative(target) + ".",
                Apply = (context, _) => Task.FromResult(MoveMod(context, mod, target)),
            },
        });
    }

    private static string MoveMod(RepairContext context, InstalledMod mod, string target)
    {
        Directory.CreateDirectory(target);

        string sourceFolder = Path.GetDirectoryName(mod.AssemblyPath);

        bool moveWholeFolder = mod.Kind != ModKind.MelonMod
                               && mod.Location is ModLocation.ModsSubfolder or ModLocation.TooDeep or ModLocation.BepInExPatchers
                               && !string.IsNullOrEmpty(sourceFolder)
                               && !string.Equals(sourceFolder, context.Install.ModsDirectory, StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(sourceFolder, context.Install.GameDirectory, StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(sourceFolder, context.Install.BepInExPluginsDirectory, StringComparison.OrdinalIgnoreCase);

        if (moveWholeFolder && Directory.Exists(sourceFolder))
        {
            foreach (var file in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(target, Path.GetRelativePath(sourceFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination, overwrite: true);
            }

            TryDeleteEmptyTree(sourceFolder);
            if (!string.Equals(mod.Directory, sourceFolder, StringComparison.OrdinalIgnoreCase))
                TryDeleteEmptyTree(mod.Directory);

            return "Moved " + mod.Name + " to " + context.Install.Relative(target) + ".";
        }

        foreach (var extension in new[] { ".dll", ".pdb", ".xml" })
        {
            string source = Path.ChangeExtension(mod.AssemblyPath, extension);
            if (!File.Exists(source)) continue;
            File.Move(source, Path.Combine(target, Path.GetFileName(source)), overwrite: true);
        }

        return "Moved " + mod.Name + " to " + context.Install.Relative(target) + ".";
    }

    private static void TryDeleteEmptyTree(string directory)
    {
        try
        {
            if (Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0)
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Leaving an empty folder behind is harmless
        }
    }

    private static void CheckModSupport(ScanResult scan, InstalledMod mod)
    {
        string reason = mod.Kind.UnsupportedReason();
        if (reason is null) return;

        bool il2cpp = mod.Kind == ModKind.Il2CppBuild;
        Add(scan, mod, new Diagnostic
        {
            Code = il2cpp ? "mod.il2cpp" : "mod.unsupported",
            Severity = Severity.Warning,
            Title = il2cpp ? mod.Name + " is an IL2CPP build" : mod.Name + " is not supported",
            Detail = il2cpp ? reason : reason + " Uses unsupported features.",
            Path = mod.AssemblyPath,
            Mod = mod,
            Repair = new Repair
            {
                Label = "Quarantine",
                Description = "Quarantine " + Path.GetFileName(mod.AssemblyPath),
                IsDestructive = true,
                Apply = (context, _) =>
                {
                    if (!File.Exists(mod.AssemblyPath)) return Task.FromResult("Already removed.");
                    context.Quarantine.TakeFile(mod.AssemblyPath);
                    return Task.FromResult("Quarantined " + mod.Name);
                },
            },
        });
    }

    private static void CheckModCompanions(ScanResult scan, InstalledMod mod)
    {
        foreach (var companion in mod.Companions.Where(c => c.Kind == ModKind.LoaderRuntime))
        {
            string path = companion.Path;
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.bundled.runtime",
                Severity = Severity.Error,
                Title = mod.Name + " includes a copy of " + companion.FileName,
                Detail = "The additional loader is not needed and can cause bugs.",
                Path = path,
                Mod = mod,
                Repair = new Repair
                {
                    Label = "Quarantine",
                    Description = "Quarantine " + scan.Install.Relative(path),
                    Apply = (context, _) =>
                    {
                        if (!File.Exists(path)) return Task.FromResult("Already removed.");
                        context.Quarantine.TakeFile(path);
                        return Task.FromResult("Quarantined " + companion.FileName + ".");
                    },
                },
            });
        }
    }

    private static void CheckModReferences(ScanResult scan, InstalledMod mod)
    {
        if (mod.Probe is null) return;

        if (mod.Probe.MissingReferences.Count > 0)
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.references.missing",
                Severity = Severity.Warning,
                Title = mod.Name + " is missing a dependency",
                Detail = "Missing reference " + string.Join(", ", mod.Probe.MissingReferences),
                Path = mod.AssemblyPath,
                Mod = mod,
            });

        if (mod.Probe.ReadError is not null && mod.Kind.IsRunnable())
            Add(scan, mod, new Diagnostic
            {
                Code = "mod.metadata",
                Severity = Severity.Warning,
                Title = mod.Name + ": " + mod.Probe.ReadError,
                Detail = "Will likely not load correctly.",
                Path = mod.AssemblyPath,
                Mod = mod,
            });
    }

    private static void CheckModLoaderVersion(ScanResult scan, InstalledMod mod)
    {
        string required = mod.Manifest?.LoaderVersion;
        if (string.IsNullOrWhiteSpace(required) || scan.Loader.Version is null) return;

        if (!mod.Kind.IsRunnable()) return;
        if (!VersionConstraint.TryParse(required, out var constraint)) return;
        if (constraint.Satisfies(scan.Loader.Version)) return;

        Add(scan, mod, new Diagnostic
        {
            Code = "mod.loaderversion",
            Severity = Severity.Error,
            Title = mod.Name + " needs a newer Mod Loader version",
            Detail = "Minimum version " + required + " is required, but " + scan.Loader.Version + " is installed.",
            Path = mod.Manifest.Path,
            Mod = mod,
            Repair = new Repair
            {
                Label = "Update",
                Description = "Downloads and installs the latest DnW Mod Loader release.",
                NeedsNetwork = true,
                Apply = InstallLatestLoaderAsync,
            },
        });
    }

    private static void CheckDependencies(ScanResult scan, ModCatalog catalog)
    {
        var present = new HashSet<string>(
            scan.Mods.Where(m => m.Kind.IsRunnable()).Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var mod in scan.Mods)
        {
            var required = mod.Manifest?.Dependencies?.Where(d => !d.Optional).ToList() ?? new List<ManifestDependency>();

            foreach (var dependency in required)
            {
                var installed = scan.ById(dependency.Id);

                if (installed is null)
                {
                    var known = catalog?.ById(dependency.Id);
                    Add(scan, mod, new Diagnostic
                    {
                        Code = "mod.dependency.missing",
                        Severity = Severity.Error,
                        Title = mod.Name + " requires " + (known?.Name ?? dependency.Id),
                        Detail = "Dependencies for " + mod.Name + " are missing: "
                                 + (known?.Name ?? dependency.Id),
                        Path = mod.Directory,
                        Mod = mod,
                    });
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(dependency.Version)
                    && VersionConstraint.TryParse(dependency.Version, out var constraint)
                    && !constraint.Satisfies(installed.Version))
                    Add(scan, mod, new Diagnostic
                    {
                        Code = "mod.dependency.version",
                        Severity = Severity.Error,
                        Title = mod.Name + " requires " + installed.Name + " " + dependency.Version,
                        Detail = installed.Name + " " + installed.Version + " is installed.",
                        Path = mod.Directory,
                        Mod = mod,
                    });
                else if (!installed.Enabled)
                    Add(scan, mod, new Diagnostic
                    {
                        Code = "mod.dependency.disabled",
                        Severity = Severity.Warning,
                        Title = mod.Name + " requires " + installed.Name,
                        Detail = installed.Name + " is disabled.",
                        Path = mod.Directory,
                        Mod = mod,
                    });
            }
        }
    }

    private static void CheckDuplicateIds(ScanResult scan)
    {
        var groups = scan.Mods
            .Where(m => m.Kind.IsRunnable() && !string.IsNullOrEmpty(m.Id))
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var copies = group.ToList();
            Add(scan, copies[0], new Diagnostic
            {
                Code = "mod.id.duplicate",
                Severity = Severity.Error,
                Title = "Two copies of " + copies[0].Name + " are installed",
                Detail = "Duplicate mod ID: " + group.Key + ".",
                Path = copies[1].AssemblyPath,
                Mod = copies[1],
            });
        }
    }

    private static void Add(ScanResult scan, InstalledMod mod, Diagnostic diagnostic)
    {
        scan.Diagnostics.Add(diagnostic);
        mod?.Issues.Add(diagnostic);
    }
}
