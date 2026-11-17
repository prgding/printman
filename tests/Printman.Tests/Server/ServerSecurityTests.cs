using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Printman.Core.Abstractions;
using Printman.Core.Models;
using Printman.Server;
using Printman.Services;
using Printman.Services.Ipp;
using Printman.Tests.Fakes;
using Printman.Tests.Support;

namespace Printman.Tests.Server;

[TestClass]
[DoNotParallelize]
public sealed class ServerSecurityTests
{
    [TestMethod]
    public async Task AnonymousIppUses10631AndPureShareHidesWebRoutes()
    {
        await using var server = new ServerFixture();
        await server.StartAsync(web: false, ippPort: 10631);
        Assert.AreEqual(10631, server.Settings.IppPort);
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.Client.GetAsync("/api/printers")).StatusCode);
        using var content = new ByteArrayContent(IppTestMessages.Body(IppTestMessages.New(IppOperation.GetPrinterAttributes)));
        content.Headers.ContentType = new("application/ipp");
        var response = await server.Client.PostAsync("/ipp/print/fake-printer", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(IppStatus.Ok, IppMessageReader.Parse(await response.Content.ReadAsByteArrayAsync()).Code);
        using var invalid = new StringContent("invalid");
        invalid.Headers.ContentType = new("application/ipp-invalid");
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, (await server.Client.PostAsync("/ipp/print", invalid)).StatusCode);
    }

    [TestMethod]
    public async Task OccupiedIppPortFailsWithoutFallback()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var server = new ServerFixture();
        int result = await server.Host.RunAsync(new ServerOptions
        {
            BindAddress = "127.0.0.1", EnableWebUi = false,
            Share = new ShareOptions { Enabled = true, EnableMdns = false, IppPort = port }
        });
        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public async Task WebAuthenticationCsrfAndExactPrinterValidationApplyOnRealHttpRoutes()
    {
        await using var server = new ServerFixture();
        await server.StartAsync(web: true);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/api/printers?pin=123456")).StatusCode);
        var missingHeader = await server.Client.PostAsJsonAsync("/api/auth/verify", new { pin = "123456" });
        Assert.AreEqual(HttpStatusCode.Forbidden, missingHeader.StatusCode);
        server.Client.DefaultRequestHeaders.Add("X-Requested-With", "Printman");
        server.Client.DefaultRequestHeaders.Add("Origin", "http://evil.example");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await server.Client.PostAsJsonAsync("/api/auth/verify", new { pin = "123456" })).StatusCode);
        server.Client.DefaultRequestHeaders.Remove("Origin");
        var login = await server.Client.PostAsJsonAsync("/api/auth/verify", new { pin = "123456" });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        Assert.IsTrue(login.Headers.GetValues("Set-Cookie").Single().Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(HttpStatusCode.OK, (await server.Client.GetAsync("/api/printers")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.Client.GetAsync("/api/queue?printer=Unknown")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.Client.PostAsJsonAsync("/api/queue/cancel", new { printer = @"\\attacker\queue", jobId = "1" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await server.Client.PostAsJsonAsync("/api/queue/purge", new { printer = "Fake" })).StatusCode);
        var traversal = await server.Client.PostAsJsonAsync("/api/print", new { printer = "Fake Printer", items = new[] { new { fileId = "../secret", copies = 1 } } });
        Assert.AreEqual(HttpStatusCode.BadRequest, traversal.StatusCode);
        Assert.AreEqual(0, server.Pipeline.Enqueued.Count);
        Assert.AreEqual(0, server.Queue.CancelledSpoolerJobs.Count);
        Assert.AreEqual(0, server.Queue.PurgedPrinters.Count);
        server.Pipeline.TicketFactory = batch =>
        {
            var ticket = new PipelineTicket(batch);
            ticket.Finish(PipelineJobState.Failed, "full");
            return ticket;
        };
        var full = await server.Client.PostAsJsonAsync("/api/print", new { printer = "Fake Printer", items = new[] { new { fileId = "abcdef0123456789", copies = 1 } } });
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, full.StatusCode);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/printers");
        rebound.Headers.Host = "attacker.example";
        Assert.AreEqual(HttpStatusCode.BadRequest, (await server.Client.SendAsync(rebound)).StatusCode);
    }

    [TestMethod]
    public void WebUiUsesDataBoundCancelHandlerAndCookieAuthenticatedEvents()
    {
        Assert.IsFalse(WebAssets.IndexHtml.Contains("onclick=\"cancelJob("));
        Assert.IsTrue(WebAssets.IndexHtml.Contains("card.querySelector('.btn-cancel-job').addEventListener('click'"));
        Assert.IsFalse(WebAssets.IndexHtml.Contains("localStorage.setItem('printman_pin'"));
        Assert.IsFalse(WebAssets.IndexHtml.Contains("/api/events?pin="));
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly CancellationTokenSource _stop = new();
        private Task<int>? _running;
        public IppServerSettings Settings { get; } = new();
        public FakePrintJobPipeline Pipeline { get; } = new();
        public FakePrintQueueService Queue { get; } = new();
        public PrintingWebServerHost Host { get; }
        public HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false });

        public ServerFixture()
        {
            var discovery = new FakePrinterDiscoveryService();
            discovery.Printers.Add(TestData.Printer(isDefault: true));
            var cache = new FileCacheService(_temp.Path);
            var resolver = new FakeDocumentRendererResolver();
            var events = new PrintEventHub();
            var registry = new SharedPrinterRegistry(discovery, Queue);
            var store = new IppJobStore();
            var formats = new IppDocumentFormats([new FakeDocumentRenderer(".pdf")]);
            var builder = new IppPrinterAttributeBuilder(registry, store, formats, Settings);
            var handler = new IppRequestHandler(registry, store, builder, formats, cache, Pipeline, events, Settings);
            Host = new PrintingWebServerHost(discovery, resolver, cache, events, Queue, Pipeline, registry,
                handler, Settings, new NoDiscovery(), new NoDiscovery(), new NoDiscovery());
        }

        public async Task StartAsync(bool web, int? ippPort = null)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            int listenPort = web ? port : ippPort ?? port;
            Client.BaseAddress = new Uri($"http://127.0.0.1:{listenPort}");
            _running = Host.RunAsync(new ServerOptions
            {
                BindAddress = "127.0.0.1", Port = port, Pin = "123456", EnableWebUi = web,
                Share = new ShareOptions { Enabled = !web, EnableMdns = false, IppPort = listenPort }
            }, _stop.Token);
            for (int i = 0; i < 100; i++)
            {
                try { using var response = await Client.GetAsync("/"); return; }
                catch (HttpRequestException) { await Task.Delay(20); }
            }
            Assert.Fail("Loopback test server failed to start.");
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_running != null) await _running.WaitAsync(TimeSpan.FromSeconds(10));
            Client.Dispose();
            _stop.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class NoDiscovery : IDnsSdServiceFactory, IServiceAdvertiser, IFirewallInspector
    {
        public string HostName => "test.local";
        public IReadOnlyList<string> Warnings => [];
        public IReadOnlyList<DnsSdService> Create(IReadOnlyList<SharedPrinter> printers) => [];
        public Task StartAsync(IReadOnlyList<DnsSdService> services, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public IReadOnlyList<string> CheckInboundAccess(int tcpPort, bool needsMdns) => [];
    }
}
