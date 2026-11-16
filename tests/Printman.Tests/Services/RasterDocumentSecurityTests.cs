using System.Buffers.Binary;
using System.Reflection;
using Printman.Services.Renderers;
using Printman.Tests.Support;

namespace Printman.Tests.Services;

[TestClass]
public sealed class RasterDocumentSecurityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamingRasterPageLimitIsEnforced(bool pwg)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine(pwg ? "test.pwg" : "test.urf");
        WriteRaster(path, pwg, 1001);
        RasterDocumentRenderer renderer = pwg ? new PwgRasterDocumentRenderer() : new UrfDocumentRenderer();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => renderer.GetPageCountAsync(path));
        WriteRaster(path, pwg, 2);
        Assert.AreEqual(2, await renderer.GetPageCountAsync(path));
    }

    [TestMethod]
    public async Task RasterIndexHasABoundedNumberOfDocuments()
    {
        using var temp = new TempDirectory();
        var renderer = new UrfDocumentRenderer();
        for (int i = 0; i < 70; i++)
        {
            var path = temp.Combine(i + ".urf");
            WriteRaster(path, false, 1);
            Assert.AreEqual(1, await renderer.GetPageCountAsync(path));
        }
        var index = typeof(RasterDocumentRenderer).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        var count = (int)index.GetType().GetProperty("Count")!.GetValue(index)!;
        Assert.IsTrue(count <= 64);
    }

    private static void WriteRaster(string path, bool pwg, int pages)
    {
        using var file = File.Create(path);
        file.Write(pwg ? "RaS2"u8 : "UNIRAST\0\0\0\0\0"u8);
        var header = new byte[pwg ? 1796 : 32];
        if (pwg)
        {
            foreach (var offset in new[] { 372, 376 }) BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(offset), 1);
            foreach (var offset in new[] { 384, 388 }) BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(offset), 8);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(276), 300);
        }
        else
        {
            header[0] = 8;
            foreach (var offset in new[] { 12, 16 }) BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(offset), 1);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), 300);
        }
        for (int i = 0; i < pages; i++)
        {
            file.Write(header);
            file.Write(new byte[] { 0, 128 });
        }
    }
}
