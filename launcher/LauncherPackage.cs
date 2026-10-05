using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class LauncherPackage
{
    internal const string ManifestName = "launcher-package.json";
    internal sealed class Manifest
    {
        public string PackageId { get; set; } = "";
        public Target[] Targets { get; set; } = Array.Empty<Target>();
    }
    internal sealed class Target
    {
        public string Architecture { get; set; } = "";
        public string Executable { get; set; } = "";
    }
    internal static bool Within(string path, string root)
    {
        string full = Path.GetFullPath(path);
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    internal static ushort Machine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5a4d) return 0;
            stream.Position = 0x3c;
            int offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 6) return 0;
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) return 0;
            return reader.ReadUInt16();
        }
        catch { return 0; }
    }
    internal static bool Compatible(string path, bool arm64)
    {
        ushort machine = Machine(path);
        return machine == 0x8664 || machine == 0x14c || (arm64 && machine == 0xaa64);
    }
    internal static Manifest? Read(string root)
    {
        string file = Path.Combine(root, ManifestName);
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid package manifest.");
    }
    internal static string? Resolve(string root, bool arm64)
    {
        var manifest = Read(root);
        if (manifest == null) return null;
        if (string.IsNullOrWhiteSpace(manifest.PackageId)) throw new InvalidDataException("Package ID is missing.");
        foreach (var target in manifest.Targets.OrderBy(t => t.Architecture.Equals(arm64 ? "arm64" : "x64", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (string.IsNullOrWhiteSpace(target.Executable) || Path.IsPathRooted(target.Executable))
                throw new InvalidDataException("Package executable must use a relative path.");
            string path = Path.GetFullPath(Path.Combine(root, target.Executable));
            if (!Within(path, root)) throw new InvalidDataException("Package executable is outside the package.");
            if (!File.Exists(path)) throw new FileNotFoundException("Package executable is missing.", path);
            ushort expected = target.Architecture.ToLowerInvariant() switch { "x64" => 0x8664, "arm64" => 0xaa64, "x86" => 0x14c, _ => 0 };
            if (expected == 0 || Machine(path) != expected) throw new InvalidDataException("Executable architecture does not match the manifest.");
            if (Compatible(path, arm64)) return path;
        }
        throw new PlatformNotSupportedException("This package has no application compatible with this Windows architecture.");
    }
    internal static bool IsRunning(string exe)
    {
        string full = Path.GetFullPath(exe);
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase); }
            catch { return true; } // Do not replace potentially running files when access is denied.
            finally { p.Dispose(); }
        });
    }
    internal static bool NeedsUpdate(string source, string installed, bool arm64)
    {
        var current = Read(source);
        var old = Read(installed);
        if (current == null || old == null || current.PackageId != old.PackageId) return false;
        string target = Resolve(source, arm64)!;
        string previous = Resolve(installed, arm64)!;
        foreach (var pair in new[] {
            (target, previous),
            (Path.Combine(Path.GetDirectoryName(target)!, "WebView2", "msedgewebview2.exe"), Path.Combine(Path.GetDirectoryName(previous)!, "WebView2", "msedgewebview2.exe")),
            (Path.ChangeExtension(target, ".dll"), Path.ChangeExtension(previous, ".dll")),
            (Path.Combine(source, "StarGateLauncherAll.dll"), Path.Combine(installed, "StarGateLauncherAll.dll")) })
        {
            if (!File.Exists(pair.Item1)) continue;
            if (!File.Exists(pair.Item2)) return true;
            using var first = File.OpenRead(pair.Item1);
            using var second = File.OpenRead(pair.Item2);
            if (!System.Security.Cryptography.SHA256.HashData(first).SequenceEqual(System.Security.Cryptography.SHA256.HashData(second))) return true;
        }
        return false;
    }
    internal static void ValidateDestination(string source, string destination)
    {
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || Within(destination, source) || Within(source, destination))
            throw new IOException("Choose an installation folder outside the source package. Source and destination cannot contain one another.");
        for (string? parent = destination; parent != null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation through a folder link is not supported.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var original = Read(source);
            var installed = Read(destination);
            if (original == null || installed == null || original.PackageId != installed.PackageId)
                throw new IOException("This folder is not an installation of this package. Choose a different folder; its files will be preserved.");
        }
    }
    internal static void ProbeDestination(string destination)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar))!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The installation parent folder does not exist.");
        string probe = Path.Combine(parent, ".launcher-write-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            using var file = new FileStream(Path.Combine(probe, "write-check"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            file.WriteByte(0);
        }
        finally { Directory.Delete(probe); }
    }
    internal static string? Install(string source, string destination)
    {
        ValidateDestination(source, destination);
        string stage = destination.TrimEnd(Path.DirectorySeparatorChar) + ".install-" + Guid.NewGuid().ToString("N");
        string? backup = null;
        try
        {
            Copy(source, stage);
            if (Read(stage) != null) Resolve(stage, RuntimeInformation.OSArchitecture == Architecture.Arm64);
            if (Directory.Exists(destination))
            {
                backup = destination.TrimEnd(Path.DirectorySeparatorChar) + ".backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(destination, backup);
            }
            try { Directory.Move(stage, destination); }
            catch { if (backup != null && !Directory.Exists(destination)) Directory.Move(backup, destination); throw; }
            if (backup != null) DeleteOwnedTree(backup, Read(destination)!.PackageId);
            return null;
        }
        catch
        {
            // Preserve partial staging files for diagnosis; never delete unrelated folders.
            throw;
        }
    }
    internal static void DeleteOwnedTree(string root, string packageId)
    {
        root = Path.GetFullPath(root);
        if (Path.GetPathRoot(root) == root || Read(root)?.PackageId != packageId)
            throw new IOException("Refusing to remove a folder that is not this installation.");
        CheckTree(root);
        Directory.Delete(root, true);
    }
    private static void CheckTree(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to remove folder links.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to remove file or folder links.");
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
    private static void Copy(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Package folder links are not supported.");
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Package file links are not supported.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
        foreach (string child in Directory.GetDirectories(source)) Copy(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}
