using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
internal static class Program
{
    private static DialogResult Alert(string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0,
            FormBorderStyle = FormBorderStyle.None, Size = new System.Drawing.Size(1, 1), StartPosition = FormStartPosition.CenterScreen };
        owner.Show();
        return MessageBox.Show(owner, text, caption, buttons, icon);
    }
    [STAThread] static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        bool verify = Environment.GetCommandLineArgs().Contains("--verify-payload");
        using var mutex = new Mutex(false, @"Local\StarGateSetup-x64");
        bool owns;
        try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) { Alert("Instalator StarGate jest już uruchomiony. Sprawdź pasek zadań.", "StarGate Setup"); return; }
        string root = Path.Combine(Path.GetTempPath(), "StarGateSetup-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarGateSetup", "setup.log");
        void Log(string message) { try { Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.AppendAllText(log, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { } }
        using var form = new Form { Text = "StarGate Setup", Width = 460, Height = 180, StartPosition = FormStartPosition.CenterScreen, ControlBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, TopMost = true };
        var label = new Label { Text = "Rozpakowywanie instalatora StarGate…", Dock = DockStyle.Top, Height = 65, TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 24, Minimum = 0, Maximum = 100 };
        form.Padding = new Padding(20);
        form.Controls.Add(label); form.Controls.Add(bar);
        form.Shown += async (_, _) =>
        {
            try
            {
                Log("Extracting " + root);
                var progress = new Progress<int>(percent => { if (!form.IsDisposed) { bar.Value = percent; label.Text = $"Rozpakowywanie instalatora StarGate… {percent}%"; } });
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(root);
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("Brak archiwum instalatora.");
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                    long total = zip.Entries.Sum(e => e.Length), done = 0;
                    foreach (var entry in zip.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid archive path.");
                        if (entry.Name.Length == 0) Directory.CreateDirectory(target);
                        else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target); }
                        done += entry.Length;
                        ((IProgress<int>)progress).Report(total == 0 ? 100 : (int)(done * 100 / total));
                    }
                });
                if (!File.Exists(Path.Combine(root, "Uninstall.ps1")) || !File.Exists(Path.Combine(root, "redist", "MicrosoftEdgeWebview2Setup.exe"))) throw new IOException("Niekompletna instalacja.");
                Log("Extraction complete");
                if (!verify)
                {
                    label.Text = "Uruchamianie kreatora instalacji…";
                    form.TopMost = false;
                    using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "StarGateLauncherAll.exe")) { UseShellExecute = true, WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Normal }) ?? throw new IOException("Nie udało się uruchomić kreatora.");
                    Log("Launcher started PID=" + process.Id);
                    form.Hide();
                    await process.WaitForExitAsync();
                    Log("Launcher exited " + process.ExitCode);
                    if (process.ExitCode != 0) throw new IOException("Kreator instalacji zakończył się błędem. Zobacz: " + log);
                }
            }
            catch (Exception ex) { Log(ex.ToString()); Environment.ExitCode = 1; if (!verify) Alert(ex.Message, "StarGate Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); Log("Temporary payload removed"); } catch (Exception ex) { Log("Cleanup: " + ex.Message); }
                form.Close();
            }
        };
        try { Application.Run(form); } finally { mutex.ReleaseMutex(); }
    }
}
