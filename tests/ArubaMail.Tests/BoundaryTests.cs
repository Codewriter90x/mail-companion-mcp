using ArubaMail;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace ArubaMail.Tests;

public class BoundaryTests
{
    [Fact]
    public async Task NetworkBudgetStopsOversizedLiteralAcrossReads()
    {
        using var stream = new BudgetStream(new MemoryStream(new byte[500]), 32); var buffer = new byte[100];
        Assert.Equal(32, await stream.ReadAsync(buffer)); await Assert.ThrowsAsync<MailError>(async () => { _ = await stream.ReadAsync(buffer); });
    }
    [Fact]
    public async Task HttpRejectsBadAuthWithoutInvokingTools()
    {
        var context = new DefaultHttpContext(); var called = false;
        await PrivateHttp.Handle(context, () => { called = true; return Task.CompletedTask; }, Encoding.UTF8.GetBytes("Bearer " + new string('x', 32)));
        Assert.Equal(401, context.Response.StatusCode); Assert.False(called);
    }
    [Theory]
    [InlineData("localhost:8000", true)]
    [InlineData("aruba-mail:8000", true)]
    [InlineData("localhost.attacker.invalid:8000", false)]
    [InlineData("localhost:abc", false)]
    [InlineData("evil.invalid", false)]
    public void HostIsExactOrValidPort(string host, bool allowed) => Assert.Equal(allowed, PrivateHttp.AllowedHost(host));
}
