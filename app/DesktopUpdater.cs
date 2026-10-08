using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
namespace DesktopUpdates;

internal sealed class UpdateManifest
{
    public int Schema { get; set; }
    public string Application { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Build { get; set; } = "";
    public string ApplicationSha256 { get; set; } = "";
    public string InstallerSha256 { get; set; } = "";
    public long InstallerSize { get; set; }
}
internal static class UpdateProtocol
{
    internal const long MaximumSize = 1024L * 1024 * 1024;
    internal static UpdateManifest Parse(string json, string application)
    {
        var m = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (m == null || m.Schema != 1 || m.Application != application || m.Architecture != "x64"
            || string.IsNullOrWhiteSpace(m.Build) || m.Build.Length > 80 || !IsHash(m.ApplicationSha256) || !IsHash(m.InstallerSha256)
            || m.InstallerSize <= 0 || m.InstallerSize > MaximumSize)
            throw new InvalidDataException("Invalid update information or a package for another application.");
        return m;
    }
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    internal static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static bool IsCurrent(UpdateManifest m, string dll) => FileHash(dll).Equals(m.ApplicationSha256, StringComparison.OrdinalIgnoreCase);
    internal static void VerifyInstaller(UpdateManifest m, string file)
    {
        if (new FileInfo(file).Length != m.InstallerSize || !FileHash(file).Equals(m.InstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer verification failed. The downloaded file will not be run.");
    }
    internal static bool TrustedUrl(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
}
internal static class DesktopUpdater
{
    private static bool busy;
    internal static async Task CheckAsync(Form owner, string application)
    {
        if (busy) return;
        busy = true; string? partial = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var dialog = new Form { Text = "Application update", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(450, 155), AutoScaleMode = AutoScaleMode.Dpi, Font = new Font("Segoe UI", 10f), TopMost = owner.TopMost };
        var status = new Label { Text = "Checking for updates...", Bounds = new Rectangle(20, 20, 410, 40) };
        var progress = new ProgressBar { Bounds = new Rectangle(20, 65, 410, 20), Style = ProgressBarStyle.Marquee };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(320, 105, 110, 32) };
        dialog.Controls.AddRange(new Control[] { status, progress, cancel });
        cancel.Click += (_, _) => cancellation.Cancel();
        dialog.FormClosing += (_, e) => { if (busy) { e.Cancel = true; cancellation.Cancel(); } };
        dialog.Show(owner); owner.Enabled = false;
        try
        {
            string? token = application == "Framelesswindow" ? await ReadGitCredentialAsync(cancellation.Token) : null;
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(application + "/DesktopUpdater");
            string api = $"https://api.github.com/repos/matelv-x/{application}/releases";
            using var response = await GetAsync(client, new Uri(api + "/latest"), token, false, cancellation.Token);
            using var release = JsonDocument.Parse(await ReadMetadataAsync(response, 262144, cancellation.Token));
            string installerName = application == "Framelesswindow" ? "FramelessSetup-x64.exe" : "StarGateSetup-x64.exe";
            Uri? manifestUrl = null, installerUrl = null;
            foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
            {
                string? name = asset.GetProperty("name").GetString();
                if (name != application + "-update.json" && name != installerName) continue;
                var url = new Uri(asset.GetProperty("url").GetString()!);
                if (!url.AbsoluteUri.StartsWith(api + "/assets/", StringComparison.Ordinal) || !long.TryParse(url.Segments.Last(), out long id) || id <= 0)
                    throw new InvalidDataException("Invalid release asset address.");
                if (name == installerName) installerUrl = url; else manifestUrl = url;
            }
            if (manifestUrl == null || installerUrl == null) throw new InvalidDataException("This release does not include update information yet.");
            using var manifestResponse = await GetAsync(client, manifestUrl, token, true, cancellation.Token);
            var manifest = UpdateProtocol.Parse(await ReadMetadataAsync(manifestResponse, 16384, cancellation.Token), application);
            if (UpdateProtocol.IsCurrent(manifest, Path.Combine(AppContext.BaseDirectory, application + ".dll")))
            {
                MessageBox.Show(dialog, "You are using the latest published build.", "Application update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(dialog, $"Build {manifest.Build} is available.\n\nDownload and install now? The application will close when setup starts. Saved settings will be kept.",
                "Application update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), application, "Updates");
            Directory.CreateDirectory(cache);
            foreach (string old in Directory.EnumerateFiles(cache, "update-*.exe"))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            string installer = Path.Combine(cache, "update-" + Guid.NewGuid().ToString("N") + ".exe"); partial = installer + ".partial";
            status.Text = "Downloading update..."; progress.Style = ProgressBarStyle.Continuous;
            using var download = await GetAsync(client, installerUrl, token, true, cancellation.Token);
            if (download.Content.Headers.ContentLength is long size && size != manifest.InstallerSize) throw new InvalidDataException("Unexpected installer size.");
            await using (var source = await download.Content.ReadAsStreamAsync(cancellation.Token))
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, cancellation.Token)) != 0)
                {
                    total += count;
                    if (total > manifest.InstallerSize) throw new InvalidDataException("Installer is larger than expected.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellation.Token);
                    progress.Value = (int)(total * 100 / manifest.InstallerSize);
                    status.Text = $"Downloading: {total / 1048576.0:F1} / {manifest.InstallerSize / 1048576.0:F1} MB";
                }
            }
            status.Text = "Verifying update...";
            await Task.Run(() => UpdateProtocol.VerifyInstaller(manifest, partial), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            File.Move(partial, installer); partial = null;
            using var started = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = cache });
            if (started == null) throw new IOException("Could not start setup.");
            busy = false; dialog.Close(); owner.Enabled = true; owner.Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            or System.ComponentModel.Win32Exception or InvalidOperationException or KeyNotFoundException or UriFormatException)
        {
            MessageBox.Show(dialog, "Update could not be completed. The current application is unchanged.\n\n" + ex.Message,
                "Application update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (partial != null) try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            busy = false;
            if (!dialog.IsDisposed) dialog.Close();
            if (!owner.IsDisposed) { owner.Enabled = true; owner.Activate(); }
        }
    }
    internal static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, string? token, bool asset, CancellationToken cancellation)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            if (!UpdateProtocol.TrustedUrl(uri)) throw new InvalidDataException("Untrusted update address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            // Private credentials are sent only to the API, never redirected asset storage.
            if (uri.Host == "api.github.com" && token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? next = response.Headers.Location; response.Dispose();
                if (next == null) throw new InvalidDataException("Missing download redirect.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode; response.Dispose();
                throw new HttpRequestException($"GitHub returned HTTP {code}. Check connection and repository access.");
            }
            return response;
        }
        throw new HttpRequestException("Too many download redirects.");
    }
    private static async Task<string> ReadMetadataAsync(HttpResponseMessage response, int maximum, CancellationToken cancellation)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); byte[] buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Update information is too large.");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    internal static string FindGitExecutable()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine })
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\GitForWindows");
            if (key?.GetValue("InstallPath") is string install)
                candidates.Add(Path.Combine(install, "cmd", "git.exe"));
        }
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
            if (!string.IsNullOrEmpty(folder)) candidates.Add(Path.Combine(folder, "Git", "cmd", "git.exe"));
        // This personal installation can also use the already installed Codex Git runtime.
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "git", "cmd", "git.exe"));
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string path = folder.Trim().Trim('"');
            if (Path.IsPathFullyQualified(path)) candidates.Add(Path.Combine(path, "git.exe"));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new IOException("Git for Windows was not found. Install Git with Git Credential Manager and sign in to GitHub before updating this private application.");
    }
    internal static async Task<string> ReadGitCredentialAsync(CancellationToken cancellation)
    {
        string git = FindGitExecutable();
        var info = new ProcessStartInfo(git, "credential fill") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string gitRoot = Path.GetDirectoryName(Path.GetDirectoryName(git))!;
        info.Environment["PATH"] = string.Join(Path.PathSeparator,
            Path.GetDirectoryName(git), Path.Combine(gitRoot, "mingw64", "bin"),
            Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetEnvironmentVariable("PATH") ?? "");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GCM_INTERACTIVE"] = "never";
        using var process = Process.Start(info) ?? throw new IOException("Could not start Git Credential Manager.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.StandardInput.WriteAsync("protocol=https\nhost=github.com\nusername=matelv-x\n\n"); process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string credentials = await output; await error;
            string? password = credentials.Split('\n').FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal));
            if (process.ExitCode == 0 && password is { Length: > 9 }) return password[9..].TrimEnd('\r');
            throw new IOException("Sign in to GitHub with Git Credential Manager, then try again. Browser sign-in alone does not grant access to the private repository.");
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
}