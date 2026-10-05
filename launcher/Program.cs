using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    // Installers stored in: <base>\redist\
    private const string RedistDir = "redist";
    private const string DesktopRuntimeX64 = "dotnet-desktop-runtime-8-win-x64.exe";
    private const string DesktopRuntimeArm64 = "dotnet-desktop-runtime-8-win-arm64.exe";
    private const string DesktopRuntimeX86 = "dotnet-desktop-runtime-8-win-x86.exe";

    // Root config folder
    private static readonly string ConfigRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Universal_Launcher");

    private sealed class LauncherConfig
    {
        public bool FirstRunDone { get; set; }
        public string? InstalledBaseDir { get; set; } // where we copied the package (root folder)
        public string? LastChosenExe { get; set; }     // optional: remember last picked target exe
        public bool DesktopShortcutCreated { get; set; }
        public bool AutostartEnabled { get; set; }

        // The user-chosen name used for Desktop/Autostart + config folder
        public string? ProjectName { get; set; }

        // (internal) remember which launcher created this config
        public string? LauncherExeName { get; set; }
    }

    private static readonly JsonSerializerOptions JsonPretty = new JsonSerializerOptions { WriteIndented = true };

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string launcherExePath = GetCurrentExePath();
        string launcherExeName = Path.GetFileName(launcherExePath);
        string launcherKey = Path.GetFileNameWithoutExtension(launcherExePath);
        try
        {
            var package = LauncherPackage.Read(AppContext.BaseDirectory);
            if (package != null) launcherKey += "-" + package.PackageId;
        }
        catch { /* ResolveUniversalTarget reports invalid package details inside the main error handler. */ }
        launcherKey = SanitizeName(launcherKey);
        if (string.IsNullOrWhiteSpace(launcherKey)) launcherKey = "Launcher";

        // Bootstrap config path (unique per launcher exe name, to avoid collisions BEFORE user picks ProjectName)
        string bootstrapCfgDir = Path.Combine(ConfigRoot, launcherKey);
        string bootstrapCfgPath = Path.Combine(bootstrapCfgDir, "launcher.json");
        string bootstrapLogPath = Path.Combine(bootstrapCfgDir, "log.txt");

        // ✅ FIX: use a re-bindable delegate (Action<string>) instead of a local method group
        Action<string> Log = (msg) => SafeLog(bootstrapLogPath, msg);

        try
        {
            Log("=== Launcher started ===");
            Log("Launcher exe: " + launcherExePath);
            Log("Launcher key: " + launcherKey);
            Log("Bootstrap cfg: " + bootstrapCfgPath);

            // Current folder (where launcher is running from RIGHT NOW)
            string baseDir = EnsureTrailingSeparator(AppContext.BaseDirectory);
            Log("AppContext.BaseDirectory: " + AppContext.BaseDirectory);
            Log("baseDir: " + baseDir);

            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(baseDir, "Uninstall.ps1") + "\"") { UseShellExecute = true });
                return;
            }
            // Detect OS architecture
            bool isArm64OS = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            Log("OSArchitecture: " + RuntimeInformation.OSArchitecture);
            Log("isArm64OS: " + isArm64OS);

            // Load config from bootstrap location
            var cfg = LoadConfigSafe(bootstrapCfgPath);

            // If user already chose ProjectName, switch config to that folder (unique per PROJECT)
            // This makes: Desktop name == Autostart name == config folder == json owner
            if (!string.IsNullOrWhiteSpace(cfg.ProjectName))
            {
                string projectKey = SanitizeName(cfg.ProjectName!);
                if (string.IsNullOrWhiteSpace(projectKey)) projectKey = launcherKey;

                string projectCfgDir = Path.Combine(ConfigRoot, projectKey);
                string projectCfgPath = Path.Combine(projectCfgDir, "launcher.json");
                string projectLogPath = Path.Combine(projectCfgDir, "log.txt");

                // ✅ rebind logger to project log
                Log = (msg) => SafeLog(projectLogPath, msg);

                Log("=== Switched to project config ===");
                Log("ProjectName: " + cfg.ProjectName);
                Log("Project cfg: " + projectCfgPath);

                // load from project config if exists; else migrate bootstrap config to project path
                if (File.Exists(projectCfgPath))
                {
                    cfg = LoadConfigSafe(projectCfgPath);
                }
                else
                {
                    Directory.CreateDirectory(projectCfgDir);
                    cfg.LauncherExeName = launcherExeName;
                    SaveConfigSafe(projectCfgPath, cfg);

                    // delete bootstrap config/log to avoid confusion
                    SaveConfigSafe(bootstrapCfgPath, cfg);
                    TryDeleteFile(bootstrapLogPath);
                }

                // update bootstrap paths to point to project config (for rest of code)
                bootstrapCfgDir = projectCfgDir;
                bootstrapCfgPath = projectCfgPath;
                bootstrapLogPath = projectLogPath;
            }

            Log("cfg.FirstRunDone: " + cfg.FirstRunDone);
            Log("cfg.ProjectName: " + (cfg.ProjectName ?? "<null>"));
            Log("cfg.InstalledBaseDir: " + (cfg.InstalledBaseDir ?? "<null>"));
            Log("cfg.LastChosenExe: " + (cfg.LastChosenExe ?? "<null>"));

            // Update only an installation owned by this exact package; preserve its config and shortcuts.
            if (cfg.FirstRunDone && !string.IsNullOrWhiteSpace(cfg.InstalledBaseDir)
                && Directory.Exists(cfg.InstalledBaseDir) && !PathsEqual(baseDir, cfg.InstalledBaseDir)
                && LauncherPackage.NeedsUpdate(baseDir, cfg.InstalledBaseDir, isArm64OS))
            {
                string installedExe = LauncherPackage.Resolve(cfg.InstalledBaseDir, isArm64OS)!;
                if (LauncherPackage.IsRunning(installedExe))
                {
                    ShowInstallerMessage("Close the installed StarGateWebView application, then run this launcher again to update it. Your settings and shortcuts will be kept.",
                        "StarGate update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string? backup = LauncherPackage.Install(baseDir.TrimEnd(Path.DirectorySeparatorChar), cfg.InstalledBaseDir.TrimEnd(Path.DirectorySeparatorChar));
                cfg.LastChosenExe = LauncherPackage.Resolve(cfg.InstalledBaseDir, isArm64OS);
                SaveConfigSafe(bootstrapCfgPath, cfg);
                RegisterUninstall(cfg.InstalledBaseDir, cfg.ProjectName ?? "StarGateWebView");
                Log("Updated existing package; old version removed.");
            }
            // If installed previously, prefer that stable location (if exists)
            if (cfg.FirstRunDone && !string.IsNullOrWhiteSpace(cfg.InstalledBaseDir) && Directory.Exists(cfg.InstalledBaseDir))
            {
                baseDir = EnsureTrailingSeparator(cfg.InstalledBaseDir);
                Log("Using InstalledBaseDir as baseDir: " + baseDir);
            }

            // Resolve target exe (UNIVERSAL)
            var resolved = ResolveUniversalTarget(baseDir, launcherExeName, isArm64OS, cfg.LastChosenExe);
            if (resolved == null)
            {
                ShowInstallerMessage(
                    "No launchable EXE was found.\n\n" +
                    "Expected layouts:\n" +
                    "- RootLauncher\\SomeApp.exe\n" +
                    "- RootLauncher\\SomeFolder\\SomeApp.exe\n\n" +
                    "Tip: Make sure your app EXE is NOT the launcher itself.",
                    "Universal Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return;
            }

            string targetExe = resolved.Value.TargetExe;
            string targetDir = resolved.Value.TargetDir;

            Log("Resolved targetDir: " + targetDir);
            Log("Resolved targetExe: " + targetExe);

            // Run first-run wizard ONCE
            if (!cfg.FirstRunDone)
            {
                Log("First run wizard starting...");
                if (!RunFirstRunWizard(baseDir, targetExe, targetDir, ref cfg, isArm64OS, bootstrapCfgPath, Log)) return;
                Log("First run wizard finished.");

                // After wizard we may have copied to new baseDir
                if (!string.IsNullOrWhiteSpace(cfg.InstalledBaseDir) && Directory.Exists(cfg.InstalledBaseDir))
                {
                    baseDir = EnsureTrailingSeparator(cfg.InstalledBaseDir);
                    Log("Post-wizard baseDir: " + baseDir);

                    resolved = ResolveUniversalTarget(baseDir, launcherExeName, isArm64OS, cfg.LastChosenExe);
                    if (resolved == null)
                    {
                        ShowInstallerMessage(
                            "Install finished, but no EXE was found in the installed folder.",
                            "Universal Launcher",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                        return;
                    }

                    targetExe = resolved.Value.TargetExe;
                    targetDir = resolved.Value.TargetDir;

                    Log("Post-wizard targetDir: " + targetDir);
                    Log("Post-wizard targetExe: " + targetExe);
                }
            }

            // 1) Check Desktop Runtime 8
            if (!HasWindowsDesktopRuntime8(targetExe))
            {
                Log(".NET Desktop Runtime 8 NOT detected.");

                var result = ShowInstallerMessage(
                    ".NET Desktop Runtime 8 is not installed.\n\n" +
                    "Click OK to install it from the local installer included with this package.\n\n" +
                    "Windows may ask for administrator permission (UAC).",
                    "Universal Launcher",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information
                );

                if (result != DialogResult.OK)
                {
                    Log("User canceled runtime install.");
                    return;
                }

                string installer = Path.Combine(
                    baseDir,
                    RedistDir,
                    LauncherPackage.Machine(targetExe) switch { 0xaa64 => DesktopRuntimeArm64, 0x14c => DesktopRuntimeX86, _ => DesktopRuntimeX64 }
                );

                Log("Runtime installer path: " + installer);

                if (!File.Exists(installer))
                {
                    Log("ERROR: runtime installer not found.");
                    ShowInstallerMessage(
                        $"Runtime installer not found:\n{installer}\n\n" +
                        "Make sure the installer exists in the 'redist' folder.",
                        "Universal Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return;
                }

                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = installer,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installer)!
                });

                if (p == null)
                {
                    Log("ERROR: failed to start runtime installer.");
                    ShowInstallerMessage(
                        "Failed to start the .NET Desktop Runtime installer.",
                        "Universal Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return;
                }

                p.WaitForExit();
                Log("Runtime installer exited with code: " + p.ExitCode);

                if (!HasWindowsDesktopRuntime8(targetExe))
                {
                    Log("ERROR: runtime still not detected after install.");
                    ShowInstallerMessage(
                        "The .NET Desktop Runtime 8 was not detected after installation.\n\n" +
                        "Open CMD and run:\n" +
                        "\"%ProgramFiles%\\dotnet\\dotnet.exe\" --list-runtimes\n\n" +
                        "If you do not see Microsoft.WindowsDesktop.App 8.0.x, install manually.",
                        "Universal Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return;
                }
            }
            else
            {
                Log(".NET Desktop Runtime 8 detected.");
            }

            EvergreenRuntime.Ensure(baseDir, Log);
            // 2) Launch the real application
            Log("Launching targetExe...");
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = targetDir,
                UseShellExecute = true
            });
            Log("Process.Start called for targetExe.");
        }
        catch (Exception ex)
        {
            SafeLog(bootstrapLogPath, "FATAL EXCEPTION: " + ex);
            ShowInstallerMessage(
                ex is UnauthorizedAccessException
                    ? "Windows denied access to the installation folder. Choose a folder under your user account (LocalAppData\\Programs), then run setup again."
                    : ex.Message,
                "Universal Launcher - Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        finally
        {
            SafeLog(bootstrapLogPath, "=== Launcher ended ===");
        }
    }

    // ============================================================
    // UNIVERSAL TARGET RESOLUTION
    // ============================================================

    private readonly struct ResolvedTarget
    {
        public readonly string TargetExe;
        public readonly string TargetDir;
        public ResolvedTarget(string exe, string dir) { TargetExe = exe; TargetDir = dir; }
    }

    private static ResolvedTarget? ResolveUniversalTarget(string baseDir, string launcherExeName, bool isArm64OS, string? lastChosenExe)
    {
        string? packagedExe = LauncherPackage.Resolve(baseDir, isArm64OS);
        if (packagedExe != null) return new ResolvedTarget(packagedExe, Path.GetDirectoryName(packagedExe)!);

        if (!string.IsNullOrWhiteSpace(lastChosenExe) && File.Exists(lastChosenExe)
            && LauncherPackage.Within(lastChosenExe, baseDir)
            && !Path.GetFileName(lastChosenExe).Equals(launcherExeName, StringComparison.OrdinalIgnoreCase)
            && LauncherPackage.Compatible(lastChosenExe, isArm64OS))
            return new ResolvedTarget(lastChosenExe, Path.GetDirectoryName(lastChosenExe)!);

        var subDirs = Directory.Exists(baseDir)
            ? Directory.GetDirectories(baseDir).Where(d => !new[] { "redist", "bin", "obj", "WebView2", ".git", ".vs" }.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)).Select(d => new DirectoryInfo(d)).ToList()
            : new List<DirectoryInfo>();

        // Prefer arch-ish folders if present
        var ranked = subDirs
            .Select(di => new { Dir = di.FullName, Score = ScoreFolderName(di.Name, isArm64OS) })
            .OrderByDescending(x => x.Score)
            .ToList();

        foreach (var f in ranked)
        {
            var exes = SafeListExes(f.Dir, launcherExeName);
            if (exes.Count == 1)
                return new ResolvedTarget(exes[0], Path.GetDirectoryName(exes[0])!);
        }

        var rootExes = SafeListExes(baseDir, launcherExeName);
        if (rootExes.Count == 1)
            return new ResolvedTarget(rootExes[0], Path.GetDirectoryName(rootExes[0])!);

        var all = new List<string>();
        all.AddRange(rootExes);
        foreach (var d in subDirs.Select(d => d.FullName))
            all.AddRange(SafeListExes(d, launcherExeName));

        all = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0) return null;
        if (all.Count == 1) return new ResolvedTarget(all[0], Path.GetDirectoryName(all[0])!);

        string? picked = PickExeFromList(all);
        if (string.IsNullOrWhiteSpace(picked)) return null;

        return new ResolvedTarget(picked, Path.GetDirectoryName(picked)!);
    }

    private static int ScoreFolderName(string folderName, bool isArm64OS)
    {
        string n = folderName.ToLowerInvariant();
        int score = 0;

        if (isArm64OS)
        {
            if (n.Contains("arm64")) score += 100;
            if (n.Contains("aarch64")) score += 80;
            if (n.Contains("x64")) score -= 30;
            if (n.Contains("x86")) score -= 60;
        }
        else
        {
            if (n.Contains("x64")) score += 100;
            if (n.Contains("win-x64")) score += 80;
            if (n.Contains("arm64")) score -= 40;
            if (n.Contains("x86")) score -= 10;
        }

        return score;
    }

    private static List<string> SafeListExes(string dir, string launcherExeName)
    {
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();

            return Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(p =>
                {
                    string name = Path.GetFileName(p);
                    if (name.Equals(launcherExeName, StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.EndsWith(".vshost.exe", StringComparison.OrdinalIgnoreCase)) return false;
                    return LauncherPackage.Compatible(p, RuntimeInformation.OSArchitecture == Architecture.Arm64);
                })
                .OrderByDescending(p => new FileInfo(p).Length)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string? PickExeFromList(List<string> exes)
    {
        using var dlg = new Form
        {
            Text = "Select application to launch",
            StartPosition = FormStartPosition.CenterScreen,
            Width = 900,
            Height = 420,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        list.Items.AddRange(exes.Cast<object>().ToArray());

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 52 };

        var btnOk = new Button
        {
            Text = "OK",
            Width = 120,
            Height = 32,
            Left = dlg.ClientSize.Width - 260,
            Top = 10,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            Width = 120,
            Height = 32,
            Left = dlg.ClientSize.Width - 130,
            Top = 10,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };

        btnOk.Click += (_, __) =>
        {
            if (list.SelectedItem == null)
            {
                ShowInstallerMessage("Select an EXE from the list.", "Universal Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        btnCancel.Click += (_, __) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

        list.DoubleClick += (_, __) =>
        {
            if (list.SelectedItem != null)
            {
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            }
        };

        panel.Controls.Add(btnOk);
        panel.Controls.Add(btnCancel);

        dlg.Controls.Add(list);
        dlg.Controls.Add(panel);

        dlg.TopMost = true;
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        return list.SelectedItem?.ToString();
    }

    private static string GetCurrentExePath()
    {
        try
        {
            string? p = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(p)) return p;
        }
        catch { }
        return Application.ExecutablePath;
    }

    // ============================================================
    // First-run wizard (asks for ProjectName and uses it for .json + shortcuts)
    // ============================================================

    private static bool RunFirstRunWizard(
        string currentBaseDir,
        string currentTargetExe,
        string currentTargetDir,
        ref LauncherConfig cfg,
        bool isArm64OS,
        string bootstrapCfgPath,
        Action<string> Log)
    {
        // Ask how user wants to name the project/shortcuts/config
        string defaultName = Path.GetFileNameWithoutExtension(currentTargetExe);
        defaultName = SanitizeName(defaultName);
        if (string.IsNullOrWhiteSpace(defaultName)) defaultName = "App";

        string? projectName = AskText(
            title: "Project name",
            prompt: "How do you want to name this project?\n\nThis name will be used for:\n- Desktop shortcut\n- Auto-start shortcut\n- Config folder + launcher.json",
            defaultValue: defaultName);

        if (string.IsNullOrWhiteSpace(projectName))
            return false; // user canceled

        projectName = projectName.Trim();
        string projectKey = SanitizeName(projectName);
        if (string.IsNullOrWhiteSpace(projectKey))
            projectKey = defaultName;

        // Move config to project folder NOW
        string projectCfgDir = Path.Combine(ConfigRoot, projectKey);
        string projectCfgPath = Path.Combine(projectCfgDir, "launcher.json");
        string projectLogPath = Path.Combine(projectCfgDir, "log.txt");

        Directory.CreateDirectory(projectCfgDir);

        cfg.ProjectName = projectName;
        cfg.LauncherExeName = Path.GetFileName(GetCurrentExePath());

        SaveConfigSafe(projectCfgPath, cfg);

        // remove bootstrap config so multiple launchers won't collide
        SaveConfigSafe(bootstrapCfgPath, cfg);

        // rebind logger to project log (local wizard logs)
        Action<string> Log2 = (msg) => SafeLog(projectLogPath, msg);
        Log2("=== Wizard started ===");
        Log2("ProjectName: " + projectName);
        Log2("Project cfg: " + projectCfgPath);

        ShowInstallerMessage(
            "First run setup\n\n" +
            "If you ever want to reset this wizard:\n\n" +
            $"Delete the file:\n%LOCALAPPDATA%\\Universal_Launcher\\{projectKey}\\launcher.json\n\n" +
            "Click OK to continue.",
            "Universal Launcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );

        var desktopAns = ShowInstallerMessage(
            "Do you want to create a Desktop shortcut?",
            "Universal Launcher",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        bool createDesktop = desktopAns == DialogResult.Yes;

        var autoAns = ShowInstallerMessage(
            "Do you want to enable Auto-Start (run at Windows login) for the current user?",
            "Universal Launcher",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        bool enableAutostart = autoAns == DialogResult.Yes;

        string recommendedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        Directory.CreateDirectory(recommendedRoot);
        using var fbd = new FolderBrowserDialog
        {
            Description = "Choose an installation folder. The recommended location does not require administrator rights.",
            UseDescriptionForTitle = true,
            SelectedPath = recommendedRoot
        };
        string destBaseDir;
        while (true)
        {
            if (ShowInstallerFolder(fbd) != DialogResult.OK || string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                Log2("Install location canceled.");
                return false;
            }
            destBaseDir = EnsureTrailingSeparator(Path.Combine(fbd.SelectedPath.Trim(), projectKey));
            try
            {
                LauncherPackage.ValidateDestination(currentBaseDir, destBaseDir);
                LauncherPackage.ProbeDestination(destBaseDir);
                break;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Log2("Installation location rejected: " + ex.Message);
                ShowInstallerMessage("This location cannot be used for installation.\n\n" + ex.Message +
                    "\n\nChoose a writable folder. Recommended:\n" + Path.Combine(recommendedRoot, projectKey),
                    "Choose another location", MessageBoxButtons.OK, MessageBoxIcon.Information);
                fbd.SelectedPath = recommendedRoot;
            }
        }
        // ensure app not running
        if (!EnsureExeIsNotRunning(currentTargetExe))
        {
            Log2("Target app was running; user canceled closing it.");
            return false;
        }

        string existingExe = Path.Combine(destBaseDir, MakeRelative(currentBaseDir, currentTargetExe));
        if (File.Exists(existingExe) && !EnsureExeIsNotRunning(existingExe))
        {
            Log2("Installed application was running; user canceled closing it.");
            return false;
        }
        string? backup = LauncherPackage.Install(currentBaseDir.TrimEnd(Path.DirectorySeparatorChar), destBaseDir.TrimEnd(Path.DirectorySeparatorChar));
        if (backup != null) Log2("Previous installation backup: " + backup);

        // Preserve relative path of target exe if possible
        string rel = MakeRelative(currentBaseDir, currentTargetExe);
        string installedCandidate = Path.Combine(destBaseDir, rel);

        string finalExe;
        string finalDir;

        if (File.Exists(installedCandidate))
        {
            finalExe = installedCandidate;
            finalDir = Path.GetDirectoryName(finalExe)!;
        }
        else
        {
            var launcherExeName = Path.GetFileName(GetCurrentExePath());
            var resolved = ResolveUniversalTarget(destBaseDir, launcherExeName, isArm64OS, null);
            if (resolved == null)
            {
                ShowInstallerMessage("Install finished, but no EXE was found in the installed folder.",
                    "Universal Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log2("ERROR: no EXE found after install.");
                return false;
            }

            finalExe = resolved.Value.TargetExe;
            finalDir = resolved.Value.TargetDir;
        }

        if (createDesktop)
        {
            CreateOrUpdateShortcut(
                shortcutPath: GetDesktopShortcutPath(projectKey),
                description: projectName,
                targetExe: finalExe,
                workingDir: finalDir,
                iconPath: PickBestIconPath(finalDir, finalExe));
        }

        if (enableAutostart)
        {
            CreateOrUpdateShortcut(
                shortcutPath: GetAutostartShortcutPath(projectKey),
                description: projectName,
                targetExe: finalExe,
                workingDir: finalDir,
                iconPath: PickBestIconPath(finalDir, finalExe));
        }

        cfg.FirstRunDone = true;
        cfg.InstalledBaseDir = destBaseDir;
        cfg.LastChosenExe = finalExe;
        cfg.DesktopShortcutCreated = createDesktop;
        cfg.AutostartEnabled = enableAutostart;
        cfg.ProjectName = projectName;

        SaveConfigSafe(projectCfgPath, cfg);
        SaveConfigSafe(bootstrapCfgPath, cfg);
        RegisterUninstall(destBaseDir, projectName);
        Log2("Wizard completed. Config saved.");
        return true;
    }

    private static void RegisterUninstall(string root, string name)
    {
        if (!File.Exists(Path.Combine(root, "Uninstall.ps1"))) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StarGateLauncher-Stargate-x64");
        key.SetValue("DisplayName", name);
        key.SetValue("Publisher", "StarGate");
        key.SetValue("InstallLocation", Path.GetFullPath(root));
        string icon = LauncherPackage.Resolve(root, RuntimeInformation.OSArchitecture == Architecture.Arm64) ?? Path.Combine(root, "window.ico");
        key.SetValue("DisplayIcon", "\"" + Path.GetFullPath(icon) + "\",0");
        key.SetValue("UninstallString", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(root, "Uninstall.ps1") + "\"");
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
    }
    // Simple input dialog (no external dependencies)
    private static Form CreateInstallerOwner()
    {
        var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0,
            FormBorderStyle = FormBorderStyle.None, Size = new System.Drawing.Size(1, 1),
            StartPosition = FormStartPosition.CenterScreen };
        owner.Show();
        return owner;
    }
    private static DialogResult ShowInstallerMessage(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using var owner = CreateInstallerOwner();
        return MessageBox.Show(owner, text, caption, buttons, icon);
    }
    private static DialogResult ShowInstallerFolder(FolderBrowserDialog dialog)
    {
        using var owner = CreateInstallerOwner();
        return dialog.ShowDialog(owner);
    }
    private static Form CreateNameDialog(string title, string prompt, string defaultValue)
    {
        var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new System.Drawing.SizeF(96, 96),
            Font = new System.Drawing.Font("Segoe UI", 10f),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16)
        };
        var layout = new TableLayoutPanel
        {
            Name = "NameLayout", Dock = DockStyle.Fill,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty,
            MinimumSize = new System.Drawing.Size(480, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Name = "NamePrompt", Text = prompt, AutoSize = true,
            MaximumSize = new System.Drawing.Size(480, 0),
            Margin = new Padding(0, 0, 0, 16)
        };
        var text = new TextBox
        {
            Name = "ProjectName", Text = defaultValue, Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 18), TabIndex = 0
        };
        var buttons = new FlowLayoutPanel
        {
            Name = "NameButtons", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false,
            Margin = Padding.Empty
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new System.Drawing.Size(100, 36), DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new System.Drawing.Size(100, 36), DialogResult = DialogResult.OK, Margin = new Padding(0, 0, 10, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(text, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { form.BringToFront(); form.Activate(); text.Focus(); text.SelectAll(); };
        return form;
    }

    private static string? AskText(string title, string prompt, string defaultValue)
    {
        using var form = CreateNameDialog(title, prompt, defaultValue);
        if (form.ShowDialog() != DialogResult.OK) return null;
        return ((TextBox)form.Controls.Find("ProjectName", true)[0]).Text;
    }
    private static string PickBestIconPath(string workingDir, string fallbackExe)
    {
        try
        {
            var ico = Directory.GetFiles(workingDir, "*.ico", SearchOption.TopDirectoryOnly)
                .OrderByDescending(p => new FileInfo(p).Length)
                .FirstOrDefault();

            return !string.IsNullOrWhiteSpace(ico) && File.Exists(ico) ? ico : fallbackExe;
        }
        catch
        {
            return fallbackExe;
        }
    }

    private static bool EnsureExeIsNotRunning(string targetExeFullPath)
    {
        try
        {
            string targetFull = Path.GetFullPath(targetExeFullPath);

            var procs = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        if (p.MainModule == null) return false;
                        string procPath = Path.GetFullPath(p.MainModule.FileName);
                        return string.Equals(procPath, targetFull, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        // access denied / 32-bit vs 64-bit → ignore
                        return false;
                    }
                })
                .ToList();

            if (procs.Count == 0)
                return true;

            var res = ShowInstallerMessage(
                $"The application is currently running:\n\n{targetFull}\n\n" +
                "It must be closed to continue installation.\n\n" +
                "Click OK to close it automatically.",
                "Universal Launcher",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (res != DialogResult.OK)
                return false;

            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited && p.CloseMainWindow())
                        p.WaitForExit(5000);

                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                    }
                }
                catch
                {
                    // ignore failures on single process
                }
            }

            // final verification
            return Process.GetProcesses().All(p =>
            {
                try
                {
                    if (p.MainModule == null) return true;
                    string procPath = Path.GetFullPath(p.MainModule.FileName);
                    return !string.Equals(procPath, targetFull, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return true;
                }
            });
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // Runtime detection
    // ============================================================

    private static bool HasWindowsDesktopRuntime8(string targetExe)
    {
        string folder = Path.GetDirectoryName(targetExe)!;
        if (File.Exists(Path.Combine(folder, "coreclr.dll")) && File.Exists(Path.Combine(folder, "System.Private.CoreLib.dll")))
            return true; // Self-contained application: no machine-wide runtime is required.
        string arch = LauncherPackage.Machine(targetExe) switch { 0xaa64 => "arm64", 0x14c => "x86", _ => "x64" };
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var registry = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var key = registry.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\" + arch + @"\sharedfx\Microsoft.WindowsDesktop.App");
            if (key != null && key.GetValueNames().Any(v => v.StartsWith("8.", StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private static string? FindDotnetExe()
    {
        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string p1 = Path.Combine(pf, "dotnet", "dotnet.exe");
            if (File.Exists(p1)) return p1;

            string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(pfx86))
            {
                string p2 = Path.Combine(pfx86, "dotnet", "dotnet.exe");
                if (File.Exists(p2)) return p2;
            }

            return "dotnet";
        }
        catch
        {
            return "dotnet";
        }
    }

    private static string RunAndCapture(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return "";

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch
        {
            return "";
        }
    }

    // ============================================================
    // Shortcuts
    // ============================================================

    private static string GetDesktopShortcutPath(string shortcutBaseName)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, shortcutBaseName + ".lnk");
    }

    private static string GetAutostartShortcutPath(string shortcutBaseName)
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startup, shortcutBaseName + ".lnk");
    }

    private static void CreateOrUpdateShortcut(string shortcutPath, string description, string targetExe, string workingDir, string iconPath)
    {
        Type? t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null)
            throw new Exception("WScript.Shell is not available on this system.");

        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(shortcutPath);

        lnk.TargetPath = targetExe;
        lnk.WorkingDirectory = workingDir;
        lnk.Description = description;
        lnk.IconLocation = File.Exists(iconPath) ? iconPath : targetExe;
        lnk.Save();
    }

    // ============================================================
    // Copy helpers
    // ============================================================

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    private static string MakeRelative(string baseDir, string fullPath)
    {
        try
        {
            string b = EnsureTrailingSeparator(Path.GetFullPath(baseDir));
            string f = Path.GetFullPath(fullPath);
            if (f.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                return f.Substring(b.Length);
        }
        catch { }
        return Path.GetFileName(fullPath);
    }

    // ============================================================
    // Config + Logging
    // ============================================================

    private static LauncherConfig LoadConfigSafe(string cfgPath)
    {
        try
        {
            if (File.Exists(cfgPath))
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(cfgPath)) ?? new LauncherConfig();
        }
        catch { }
        return new LauncherConfig();
    }

    private static void SaveConfigSafe(string cfgPath, LauncherConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
        string temp = cfgPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(cfg, JsonPretty));
            File.Move(temp, cfgPath, overwrite: true);
        }
        finally { TryDeleteFile(temp); }
    }
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void SafeLog(string logPath, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}"
            );
        }
        catch
        {
            // ignore logging errors
        }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        string s = name.Trim();

        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        // also avoid trailing dots/spaces (Windows)
        s = s.TrimEnd(' ', '.');

        // keep it reasonable
        if (s.Length > 60) s = s.Substring(0, 60);

        return s;
    }
}
