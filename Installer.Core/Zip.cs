using System.IO.Compression;

namespace PeakRelay.Installer.Core;

/// <summary>Extracts the embedded BepInEx archive (standard zip, read from a resource stream).</summary>
public static class Zip
{
    /// <summary>Extracts every entry under <paramref name="prefix"/> into targetDir. Returns file count.</summary>
    public static int Extract(Stream stream, string prefix, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var count = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
                continue;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = name[prefix.Length..].TrimStart('/');
            if (string.IsNullOrEmpty(relative))
                continue;
            var target = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!target.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"zip entry escapes target dir: {name}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!); // ZipArchive doesn't create parents
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }
}
