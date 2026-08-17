using System.Buffers;
using System.Security.Cryptography;

namespace Vaultix.Storage;

public sealed class ContentAddressedObjectStore
{
    private readonly string _root;
    private readonly string _objects;
    private readonly string _temporary;

    public ContentAddressedObjectStore(string repositoryRoot)
    {
        _root = Path.GetFullPath(repositoryRoot);
        _objects = Path.Combine(_root, "objects");
        _temporary = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_objects);
        Directory.CreateDirectory(_temporary);
    }

    public string RootPath => _root;

    public bool Exists(string hash) => File.Exists(GetObjectPath(hash));

    public FileStream OpenRead(string hash) => new(
        GetObjectPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task<long> PutAsync(string expectedHash, Stream source, long maximumBytes, CancellationToken cancellationToken)
    {
        var normalizedHash = NormalizeHash(expectedHash);
        var target = GetObjectPath(normalizedHash);
        if (File.Exists(target))
        {
            return new FileInfo(target).Length;
        }

        var temporaryPath = Path.Combine(_temporary, $"{Guid.NewGuid():N}.upload");
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long totalBytes = 0;
        try
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalBytes = checked(totalBytes + read);
                    if (totalBytes > maximumBytes)
                    {
                        throw new InvalidDataException("Das Upload-Limit wurde überschritten.");
                    }

                    incrementalHash.AppendData(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            var actualHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
            if (!actualHash.Equals(normalizedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Der übertragene Inhalt stimmt nicht mit seinem SHA-256-Hash überein.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Move(temporaryPath, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                File.Delete(temporaryPath);
            }

            return totalBytes;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public string GetObjectPath(string hash)
    {
        var normalized = NormalizeHash(hash);
        return Path.Combine(_objects, normalized[..2], normalized.Substring(2, 2), normalized);
    }

    public static string NormalizeHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Ein Objekt-Hash muss aus genau 64 Hexadezimalzeichen bestehen.", nameof(hash));
        }

        return hash.ToLowerInvariant();
    }
}
