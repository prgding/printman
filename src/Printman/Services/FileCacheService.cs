using System.Security.Cryptography;
using System.Text;
using Printman.Core.Abstractions;
using Printman.Core.Models;

namespace Printman.Services;

public class FileCacheService : IFileCacheService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".txt", ".log", ".csv", ".json", ".md",
        ".pwg", ".urf"
    };

    private readonly string _cacheDirectory;
    private readonly object _sync = new();
    private readonly Dictionary<string, FileCacheResult> _cacheIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _uploadSlots = new(4);
    private long _reservedBytes;

    public string CacheDirectory => _cacheDirectory;
    public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxCacheSizeBytes { get; set; } = 500L * 1024 * 1024;
    public bool Deduplicate { get; set; } = true;

    public FileCacheService(string? cacheDirectory = null)
    {
        _cacheDirectory = Path.GetFullPath(cacheDirectory ?? DefaultCacheDirectory(
            OperatingSystem.IsWindows(), AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Directory.CreateDirectory(_cacheDirectory);
        EnsureCacheDirectory();
        foreach (var path in Directory.GetFiles(_cacheDirectory))
        {
            if (IsTemporaryFile(path)) TryDeleteFile(path);
        }
    }

    public static string DefaultCacheDirectory(bool windows, string baseDirectory, string? xdgCacheHome, string userProfile)
    {
        if (windows) return Path.Combine(baseDirectory, "cache");
        var root = !string.IsNullOrWhiteSpace(xdgCacheHome) && Path.IsPathRooted(xdgCacheHome)
            ? xdgCacheHome : Path.Combine(userProfile, ".cache");
        return Path.Combine(root, "printman");
    }

    // Accept the original 16-hex cache identifiers as well as new random/hash identifiers.
    public static bool IsValidFileId(string? fileId) =>
        fileId is { Length: 16 or 32 or 64 } && fileId.All(char.IsAsciiHexDigit);

    public Task<FileCacheResult> StoreFileAsync(string originalFileName, Stream contentStream, CancellationToken ct = default) =>
        StoreFileAsync(originalFileName, contentStream, MaxFileSizeBytes, ct);

    public async Task<FileCacheResult> StoreFileAsync(string originalFileName, Stream contentStream, long maxFileSizeBytes, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            throw new ArgumentException($"File extension '{extension}' is not supported for printing.");
        if (!await _uploadSlots.WaitAsync(0, ct))
            throw new InvalidOperationException("Too many concurrent uploads. Try again later.");

        var tempPath = Path.Combine(_cacheDirectory, $"tmp_{Guid.NewGuid():N}.tmp");
        long reserved = 0;
        try
        {
            lock (_sync)
            {
                EnsureCacheDirectory();
                _uploads.Add(tempPath);
            }
            string hash;
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // Bind the identifier to the format as well as the bytes.
                sha.AppendData(Encoding.UTF8.GetBytes(extension + "\0"));
                var buffer = new byte[81920];
                int count;
                while ((count = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    if (reserved + count > maxFileSizeBytes)
                        throw new InvalidOperationException("Uploaded file exceeds the maximum allowed size.");
                    lock (_sync)
                    {
                        EnsureCapacity(count);
                        reserved += count;
                        _reservedBytes += count;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    sha.AppendData(buffer, 0, count);
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            lock (_sync)
            {
                EnsureCacheDirectory();
                var id = Deduplicate ? hash : Guid.NewGuid().ToString("N");
                var path = Path.Combine(_cacheDirectory, id + extension);
                bool duplicate = File.Exists(path);
                if (duplicate && !IsRegularFile(path))
                    throw new IOException("Cache destination is not a regular file.");
                if (duplicate) File.Delete(tempPath);
                else File.Move(tempPath, path);
                var result = new FileCacheResult(id, Path.GetFileName(originalFileName), path, reserved, duplicate, extension);
                _cacheIndex[id] = result;
                return result;
            }
        }
        finally
        {
            lock (_sync)
            {
                TryDeleteFile(tempPath);
                _uploads.Remove(tempPath);
                _reservedBytes -= reserved;
            }
            _uploadSlots.Release();
        }
    }

    public FileCacheResult? GetFile(string fileId)
    {
        if (!IsValidFileId(fileId) || !Directory.Exists(_cacheDirectory)) return null;
        lock (_sync)
        {
            EnsureCacheDirectory();
            if (_cacheIndex.TryGetValue(fileId, out var cached) && IsRegularFile(cached.CachedFilePath)) return cached;
            // Never interpolate an unvalidated value into a filesystem search pattern.
            var matches = Directory.GetFiles(_cacheDirectory)
                .Where(p => IsCacheFile(p) && string.Equals(Path.GetFileNameWithoutExtension(p), fileId, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length != 1) return null; // Ambiguous legacy identifiers must be re-uploaded.
            var path = matches[0];
            var result = new FileCacheResult(fileId, Path.GetFileName(path), path,
                new FileInfo(path).Length, false, Path.GetExtension(path).ToLowerInvariant());
            _cacheIndex[fileId] = result;
            return result;
        }
    }

    public bool AddReference(string fileId)
    {
        lock (_sync)
        {
            if (GetFile(fileId) == null) return false;
            _references[fileId] = _references.GetValueOrDefault(fileId) + 1;
            return true;
        }
    }

    public void ReleaseReference(string fileId, bool deleteIfUnused)
    {
        lock (_sync)
        {
            int count = _references.GetValueOrDefault(fileId);
            if (count == 0) return;
            if (count > 1) _references[fileId] = count - 1;
            else
            {
                _references.Remove(fileId);
                if (deleteIfUnused) DeleteFile(fileId);
            }
        }
    }

    public bool DeleteFile(string fileId)
    {
        if (!IsValidFileId(fileId) || !Directory.Exists(_cacheDirectory)) return false;
        lock (_sync)
        {
            if (_references.ContainsKey(fileId)) return false;
            EnsureCacheDirectory();
            bool removed = false;
            foreach (var path in Directory.GetFiles(_cacheDirectory).Where(p => IsCacheFile(p) &&
                string.Equals(Path.GetFileNameWithoutExtension(p), fileId, StringComparison.OrdinalIgnoreCase)))
                removed |= TryDeleteFile(path);
            if (removed) _cacheIndex.Remove(fileId);
            return removed;
        }
    }

    public void ClearCache()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_cacheDirectory)) return;
            EnsureCacheDirectory();
            foreach (var path in Directory.GetFiles(_cacheDirectory))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!_uploads.Contains(path) && !_references.ContainsKey(id) && (IsCacheFile(path) || IsTemporaryFile(path)))
                {
                    if (TryDeleteFile(path)) _cacheIndex.Remove(id);
                }
            }
        }
    }

    public Task CleanupOldFilesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_sync)
        {
            if (Directory.Exists(_cacheDirectory)) EnsureCapacity(0, cleanup: true);
        }
    }, ct);

    private void EnsureCapacity(long additionalBytes, bool cleanup = false)
    {
        EnsureCacheDirectory();
        var files = new DirectoryInfo(_cacheDirectory).GetFiles().Where(f => !_uploads.Contains(f.FullName)).ToList();
        long total = files.Sum(f => f.Length) + _reservedBytes + additionalBytes;
        if (total <= MaxCacheSizeBytes) return;
        long target = cleanup ? (long)(MaxCacheSizeBytes * 0.8) : MaxCacheSizeBytes;
        foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (_references.ContainsKey(id) || !IsCacheFile(file.FullName)) continue;
            long length = file.Length;
            if (TryDeleteFile(file.FullName))
            {
                total -= length;
                _cacheIndex.Remove(id);
            }
            if (total <= target) return;
        }
        if (!cleanup && total > MaxCacheSizeBytes)
            throw new InvalidOperationException("Print cache is full. Try again after queued jobs finish.");
    }

    private void EnsureCacheDirectory()
    {
        if ((File.GetAttributes(_cacheDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The cache directory must not be a symbolic link or junction.");
    }

    private static bool IsRegularFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;

    private static bool IsCacheFile(string path) => IsValidFileId(Path.GetFileNameWithoutExtension(path)) &&
        AllowedExtensions.Contains(Path.GetExtension(path)) && IsRegularFile(path);

    private static bool IsTemporaryFile(string path) => Path.GetFileName(path).StartsWith("tmp_", StringComparison.Ordinal) &&
        Path.GetExtension(path) == ".tmp" && IsRegularFile(path);

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!IsRegularFile(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
