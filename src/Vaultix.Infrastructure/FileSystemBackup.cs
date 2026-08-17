using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Vaultix.Core;

namespace Vaultix.Infrastructure;

public sealed class Sha256FileHasher : IFileHasher
{
    public async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

public sealed class DefaultExcludePolicy : IExcludePolicy
{
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".cache"
    };

    public bool IsExcluded(string rootPath, string fullPath, FileAttributes attributes)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        var canonicalPath = Path.GetFullPath(fullPath);
        if (!IsWithin(canonicalRoot, canonicalPath))
        {
            return true;
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
        {
            return true;
        }

        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        var components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (components.Any(component => component.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (ExcludedExtensions.Contains(Path.GetExtension(canonicalPath)))
        {
            return true;
        }

        return IsKnownCache(canonicalPath) || IsContextualBuildArtifact(canonicalPath, components);
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownCache(string path)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (path.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}AppData{Path.DirectorySeparatorChar}Local{Path.DirectorySeparatorChar}Google{Path.DirectorySeparatorChar}Chrome{Path.DirectorySeparatorChar}User Data{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               normalized.Contains($"{Path.DirectorySeparatorChar}Cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContextualBuildArtifact(string path, string[] components)
    {
        for (var index = 0; index < components.Length - 1; index++)
        {
            var name = components[index];
            var directory = WalkUp(path, components.Length - index - 1);
            var parent = Directory.GetParent(directory)?.FullName;
            if (parent is null)
            {
                continue;
            }

            if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(parent, "package.json")))
            {
                return true;
            }

            if ((name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)) &&
                Directory.EnumerateFiles(parent, "*.*proj", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }
        }

        return false;
    }

    private static string WalkUp(string path, int count)
    {
        var current = path;
        for (var index = 0; index < count; index++)
        {
            current = Directory.GetParent(current)?.FullName ?? current;
        }

        return current;
    }
}

public sealed class FileScanner(IExcludePolicy excludePolicy) : IFileScanner
{
    public async IAsyncEnumerable<FileCandidate> ScanAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(canonicalRoot))
        {
            throw new DirectoryNotFoundException($"Der Backup-Ordner wurde nicht gefunden: {canonicalRoot}");
        }

        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var child in EnumerateSafely(directory, directories: true))
            {
                var attributes = GetAttributesSafely(child);
                if (attributes is not null && !excludePolicy.IsExcluded(canonicalRoot, child, attributes.Value))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in EnumerateSafely(directory, directories: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    info.Refresh();
                    if (!info.Exists || excludePolicy.IsExcluded(canonicalRoot, file, info.Attributes))
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                yield return new FileCandidate(
                    canonicalRoot,
                    info.FullName,
                    Path.GetRelativePath(canonicalRoot, info.FullName),
                    info.Length,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    info.Attributes);
                await Task.Yield();
            }
        }
    }

    private static string[] EnumerateSafely(string directory, bool directories)
    {
        try
        {
            return directories ? Directory.EnumerateDirectories(directory).ToArray() : Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileAttributes? GetAttributesSafely(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
