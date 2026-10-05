using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Forms;
internal static class EvergreenRuntime
{
    internal static bool IsInstalled()
    {
        const string path = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(path);
                if (Version.TryParse(key?.GetValue("pv") as string, out var version) && version > new Version(0,0,0,0)) return true;
            } catch (System.Security.SecurityException) { } catch (UnauthorizedAccessException) { }
        }
        return false;
    }
    internal static async Task EnsureAvailableAsync(Func<bool> detected, Func<Task<int>> install, Func<Task> retryDelay)
    {
        if (detected()) return;
        int code = await install();
        for (int attempt=0; attempt<30; attempt++)
        {
            if (detected()) return;
            await retryDelay();
        }
        throw new IOException("Nie udało się zainstalować WebView2 Evergreen (kod " + code + "). Sprawdź połączenie z internetem i ponownie uruchom instalator.");
    }
    internal static void Ensure(string root, Action<string> log)
    {
        if (IsInstalled()) { log("WebView2 Evergreen detected; no installation needed."); return; }
        string installer = Path.Combine(root, "redist", "MicrosoftEdgeWebview2Setup.exe");
        if (!File.Exists(installer)) throw new FileNotFoundException("Brak instalatora WebView2 Evergreen.", installer);
        Exception? error = null;
        using var form = new Form { Text="WebView2 Evergreen", Width=480, Height=180, TopMost=true,
            StartPosition=FormStartPosition.CenterScreen, ControlBox=false, FormBorderStyle=FormBorderStyle.FixedDialog, Padding=new Padding(20) };
        form.Controls.Add(new Label { Text="Instalowanie WebView2 Evergreen…\nWymagane jest połączenie z internetem.", Dock=DockStyle.Fill, TextAlign=System.Drawing.ContentAlignment.MiddleCenter });
        form.Controls.Add(new ProgressBar { Dock=DockStyle.Bottom, Height=24, Style=ProgressBarStyle.Marquee });
        form.Shown += async (_,_) => {
            try {
                await EnsureAvailableAsync(IsInstalled, async () => {
                    log("Installing WebView2 Evergreen using Microsoft bootstrapper.");
                    using var process=Process.Start(new ProcessStartInfo(installer, "/silent /install") {
                        UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=Path.GetDirectoryName(installer)! })
                        ?? throw new IOException("Nie udało się uruchomić instalatora WebView2.");
                    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    await process.WaitForExitAsync(timeout.Token);
                    log("WebView2 bootstrapper exit code: " + process.ExitCode);
                    return process.ExitCode;
                }, () => Task.Delay(2000));
                log("WebView2 Evergreen is ready.");
            } catch(Exception ex) { error=ex; }
            finally { form.Close(); }
        };
        form.ShowDialog();
        if(error != null) throw new IOException("WebView2 Evergreen: " + error.Message, error);
    }
}
