using Microsoft.AspNetCore.Http;
using Printman.Server;

namespace Printman.Tests.Server;

[TestClass]
public sealed class WebSessionSecurityTests
{
    [TestMethod]
    public void PinAttemptsAreBoundedAndRecoverAfterWindow()
    {
        var clock = new TestClock();
        var security = new WebSessionSecurity("123456", clock);
        for (int i = 0; i < 20; i++) Assert.IsFalse(security.VerifyPin("000000"));
        Assert.IsFalse(security.VerifyPin("123456"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.IsTrue(security.VerifyPin("123456"));
        Assert.IsFalse(security.VerifyPin(null));
        Assert.IsFalse(security.VerifyPin(new string('a', 129)));
    }

    [TestMethod]
    public void SessionsExpireAndQuerySecretsAreRejected()
    {
        var clock = new TestClock();
        var security = new WebSessionSecurity("123456", clock);
        var request = new DefaultHttpContext().Request;
        var token = security.CreateSession();
        request.QueryString = new QueryString("?pin=123456&token=" + token);
        Assert.IsFalse(security.IsAuthenticated(request));
        request.Headers["Cookie"] = "printman_auth=" + token;
        Assert.IsTrue(security.IsAuthenticated(request));
        clock.Now += WebSessionSecurity.SessionLifetime;
        Assert.IsFalse(security.IsAuthenticated(request));
        request.Headers.Clear();
        request.Headers["X-Printer-Auth"] = security.CreateSession();
        Assert.IsTrue(security.IsAuthenticated(request));
        request.Headers.Clear();
        request.Headers["X-Printer-Pin"] = "123456";
        Assert.IsTrue(security.IsAuthenticated(request));
    }

    [TestMethod]
    public void SessionStoreIsBounded()
    {
        var security = new WebSessionSecurity("123456");
        var request = new DefaultHttpContext().Request;
        request.Headers["X-Printer-Auth"] = security.CreateSession();
        for (int i = 0; i < 256; i++) security.CreateSession();
        Assert.IsFalse(security.IsAuthenticated(request));
    }

    [TestMethod]
    [DataRow(null, null, false)]
    [DataRow(null, "Printman", true)]
    [DataRow("http://127.0.0.1:5000", "Printman", true)]
    [DataRow("http://127.0.0.1:5001", "Printman", false)]
    [DataRow("http://evil.example", "Printman", false)]
    [DataRow("null", "Printman", false)]
    [DataRow("http://127.0.0.1:5000", "anything", false)]
    public void OriginRequiresHeaderAndMatchingSchemeHostPort(string? origin, string? header, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("127.0.0.1", 5000);
        if (origin != null) request.Headers["Origin"] = origin;
        if (header != null) request.Headers["X-Requested-With"] = header;
        Assert.AreEqual(expected, WebSessionSecurity.IsSafeOrigin(request));
        request.Headers["Referer"] = "http://evil.example/";
        Assert.IsFalse(WebSessionSecurity.IsSafeOrigin(request));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
