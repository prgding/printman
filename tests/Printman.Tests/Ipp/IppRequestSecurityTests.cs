using System.Net;
using Printman.Core.Models;
using Printman.Services.Ipp;
using Printman.Tests.Support;

namespace Printman.Tests.Ipp;

public sealed partial class IppRequestHandlerTests
{
    [TestMethod]
    public async Task OtherAddressCannotInspectCancelOrSupplyAnotherClientsDocument()
    {
        using var h = new Harness();
        var create = IppTestMessages.New(IppOperation.CreateJob);
        create.Group(IppTag.OperationAttributes)!.AddName("requesting-user-name", "alice");
        var id = JobId(await SendAsync(h, create));
        foreach (short operation in new[] { IppOperation.GetJobAttributes, IppOperation.CancelJob, IppOperation.SendDocument, IppOperation.CloseJob })
        {
            var request = IppTestMessages.New(operation);
            request.Group(IppTag.OperationAttributes)!.AddInteger("job-id", id);
            request.Group(IppTag.OperationAttributes)!.AddName("requesting-user-name", "alice");
            var context = IppTestMessages.Context(request, IppTestMessages.PdfDoc(), remote: IPAddress.Parse("192.168.1.99"));
            var response = IppMessageReader.Parse(await h.Handler.HandleAsync(context, CancellationToken.None));
            Assert.AreEqual(IppStatus.NotFound, response.Code);
        }
        var list = IppTestMessages.New(IppOperation.GetJobs);
        var listed = IppMessageReader.Parse(await h.Handler.HandleAsync(
            IppTestMessages.Context(list, remote: IPAddress.Parse("192.168.1.99")), CancellationToken.None));
        Assert.IsNull(listed.Group(IppTag.JobAttributes));
        var cancelAll = IppTestMessages.New(IppOperation.CancelMyJobs);
        await h.Handler.HandleAsync(IppTestMessages.Context(cancelAll, remote: IPAddress.Parse("192.168.1.99")), CancellationToken.None);
        Assert.IsTrue(h.Store.Get(id)!.AwaitingDocument);
        Assert.AreEqual(0, h.Pipeline.Enqueued.Count);
        // IPv4 and IPv4-mapped IPv6 identify the same transport peer.
        var own = IppTestMessages.New(IppOperation.GetJobAttributes);
        own.Group(IppTag.OperationAttributes)!.AddInteger("job-id", id);
        var ownResponse = IppMessageReader.Parse(await h.Handler.HandleAsync(
            IppTestMessages.Context(own, remote: IPAddress.Parse("::ffff:192.168.1.50")), CancellationToken.None));
        Assert.AreEqual(IppStatus.Ok, ownResponse.Code);
    }

    [TestMethod]
    public async Task ConcurrentSendDocumentAndCancellationNeverEnqueueCanceledUpload()
    {
        using var h = new Harness();
        var id = JobId(await SendAsync(h, IppTestMessages.New(IppOperation.CreateJob)));
        var send = IppTestMessages.New(IppOperation.SendDocument);
        send.Group(IppTag.OperationAttributes)!.AddInteger("job-id", id);
        var head = IppMessageWriter.Write(send);
        using var body = new GatedBody(IppTestMessages.Body(send, IppTestMessages.PdfDoc()), head.Length);
        var first = h.Handler.HandleAsync(new IppRequestContext(body, null, IppTestMessages.Host, IPAddress.Parse("192.168.1.50")), CancellationToken.None);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = await SendAsync(h, send, IppTestMessages.PdfDoc());
            Assert.AreEqual(IppStatus.MultipleDocumentJobsNotSupported, second.Code);
            var cancel = IppTestMessages.New(IppOperation.CancelJob);
            cancel.Group(IppTag.OperationAttributes)!.AddInteger("job-id", id);
            Assert.AreEqual(IppStatus.Ok, (await SendAsync(h, cancel)).Code);
        }
        finally { body.Release.TrySetResult(); }
        await first;
        Assert.AreEqual(0, h.Pipeline.Enqueued.Count);
        Assert.AreEqual(IppJobState.Canceled, h.Store.Get(id)!.GetState().State);
        Assert.AreEqual(0, Directory.GetFiles(h.Cache.CacheDirectory).Length);
    }

    [TestMethod]
    public async Task InterruptedUploadReleasesJobCapacity()
    {
        using var h = new Harness();
        var request = IppTestMessages.New(IppOperation.PrintJob);
        using var body = new GatedBody(IppTestMessages.Body(request, IppTestMessages.PdfDoc()), IppMessageWriter.Write(request).Length);
        using var cancellation = new CancellationTokenSource();
        var pending = h.Handler.HandleAsync(new IppRequestContext(body, null, IppTestMessages.Host, IPAddress.Loopback), cancellation.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual(0, h.Store.ActiveCount("fake-printer"));
    }

    private sealed class GatedBody(byte[] bytes, int headerLength) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= headerLength)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
