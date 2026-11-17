using Printman.Services;
using Printman.Tests.Support;

namespace Printman.Tests.Services;

[TestClass]
public sealed class CacheSecurityTests
{
    [TestMethod]
    [DataRow("../outside")]
    [DataRow("..\\outside")]
    [DataRow("*")]
    [DataRow("/etc/passwd")]
    [DataRow("C:\\Windows\\win.ini")]
    [DataRow("abcdef0123456789:stream")]
    [DataRow("")]
    public void UntrustedIdsCannotReadOrDeleteFiles(string id)
    {
        using var temp = new TempDirectory();
        var outside = temp.Combine("outside.txt");
        File.WriteAllText(outside, "confidential");
        var cache = new FileCacheService(temp.Combine("cache"));
        Assert.IsNull(cache.GetFile(id));
        Assert.IsFalse(cache.DeleteFile(id));
        Assert.IsFalse(cache.AddReference(id));
        cache.ReleaseReference(id, true);
        Assert.AreEqual("confidential", File.ReadAllText(outside));
    }

    [TestMethod]
    public void LegacyAmbiguousIdsAndUnsupportedFilesAreRejected()
    {
        using var temp = new TempDirectory();
        var cache = new FileCacheService(temp.Path);
        File.WriteAllText(temp.Combine("abcdef0123456789.pdf"), "a");
        File.WriteAllText(temp.Combine("abcdef0123456789.txt"), "b");
        File.WriteAllText(temp.Combine("0123456789abcdef.exe"), "program");
        Assert.IsNull(cache.GetFile("abcdef0123456789"));
        Assert.IsNull(cache.GetFile("0123456789abcdef"));
        cache.ClearCache();
        Assert.IsTrue(File.Exists(temp.Combine("0123456789abcdef.exe")));
    }

    [TestMethod]
    public async Task ReferencesProtectFilesFromQuotaCleanupAndDeletion()
    {
        using var temp = new TempDirectory();
        var cache = new FileCacheService(temp.Path);
        var file = await cache.StoreFileAsync("a.txt", new MemoryStream(new byte[20]));
        Assert.IsTrue(cache.AddReference(file.FileId));
        Assert.IsTrue(cache.AddReference(file.FileId));
        cache.MaxCacheSizeBytes = 10;
        await cache.CleanupOldFilesAsync();
        cache.ClearCache();
        Assert.IsFalse(cache.DeleteFile(file.FileId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.StoreFileAsync("b.txt", new MemoryStream(new byte[1])));
        cache.ReleaseReference(file.FileId, true);
        Assert.IsNotNull(cache.GetFile(file.FileId));
        cache.ReleaseReference(file.FileId, true);
        Assert.IsNull(cache.GetFile(file.FileId));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length);
    }

    [TestMethod]
    public async Task UploadRejectsAggregateQuotaAndCleansTemporaryFiles()
    {
        using var temp = new TempDirectory();
        var cache = new FileCacheService(temp.Path) { MaxCacheSizeBytes = 10 };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.StoreFileAsync("large.pdf", new MemoryStream(new byte[11])));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length);
        var first = await cache.StoreFileAsync("a.txt", new MemoryStream(new byte[8]));
        await cache.StoreFileAsync("b.txt", new MemoryStream(new byte[9]));
        Assert.IsNull(cache.GetFile(first.FileId));
        Assert.IsTrue(Directory.GetFiles(temp.Path).Sum(p => new FileInfo(p).Length) <= 10);
    }

    [TestMethod]
    public void SymbolicLinksAreNeverReadOrDeleted()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Symlink creation requires Windows developer mode or privilege.");
        using var temp = new TempDirectory();
        var cache = new FileCacheService(temp.Combine("cache"));
        var outside = temp.Combine("secret.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(cache.CacheDirectory, "abcdef0123456789.txt"), outside);
        Assert.IsNull(cache.GetFile("abcdef0123456789"));
        Assert.IsFalse(cache.DeleteFile("abcdef0123456789"));
        cache.ClearCache();
        Assert.AreEqual("secret", File.ReadAllText(outside));
        var link = temp.Combine("cache-link");
        Directory.CreateSymbolicLink(link, cache.CacheDirectory);
        Assert.ThrowsExactly<IOException>(() => new FileCacheService(link));
    }
    [TestMethod]
    public async Task ConcurrentUploadSlotsAreBoundedAndReleased()
    {
        using var temp = new TempDirectory();
        var cache = new FileCacheService(temp.Path) { Deduplicate = false };
        var streams = Enumerable.Range(0, 4).Select(_ => new GatedUpload()).ToArray();
        var uploads = streams.Select(stream => cache.StoreFileAsync("a.txt", stream)).ToArray();
        await Task.WhenAll(streams.Select(stream => stream.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.StoreFileAsync("a.txt", new MemoryStream([1])));
        }
        finally { foreach (var stream in streams) stream.Release.TrySetResult(); }
        await Task.WhenAll(uploads);
        Assert.AreEqual(4, Directory.GetFiles(temp.Path).Length);
        cache.ClearCache();
        Assert.IsNotNull(await cache.StoreFileAsync("after.txt", new MemoryStream([1])));
        foreach (var stream in streams) stream.Dispose();
    }

    private sealed class GatedUpload() : MemoryStream(new byte[] { 1 })
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

}
