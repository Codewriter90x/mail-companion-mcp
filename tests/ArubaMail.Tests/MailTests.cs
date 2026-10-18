using System.Text;
using System.Text.Json;
using ArubaMail;
using MailKit;
using MailKit.Search;
using MimeKit;
using NSubstitute;

namespace ArubaMail.Tests;

public sealed class MailTests
{
    private readonly IMailConnection connection = Substitute.For<IMailConnection>();
    private readonly IMailFolder box = Substitute.For<IMailFolder>();
    private readonly IMessageSummary summary = Substitute.For<IMessageSummary>();
    private readonly MailService mail;
    public MailTests()
    {
        box.UidValidity.Returns(77u); connection.Open(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(box);
        summary.UniqueId.Returns(new UniqueId(42)); summary.Size.Returns(3000u); summary.Flags.Returns(MessageFlags.None);
        var envelope = new Envelope { Subject = "Prova", MessageId = "test@example.invalid" }; envelope.From.Add(new MailboxAddress("Anna", "anna@example.invalid")); summary.Envelope.Returns(envelope);
        box.FetchAsync(Arg.Any<IList<UniqueId>>(), Arg.Any<IFetchRequest>(), Arg.Any<CancellationToken>()).Returns(new List<IMessageSummary> { summary });
        box.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns(new List<UniqueId> { new(41), new(42) });
        mail = new MailService((_, _) => Task.FromResult(connection));
    }
    [Fact]
    public async Task ListingRequestsHeadersOnlyAndReadOnly()
    {
        var output = JsonSerializer.SerializeToNode(await mail.List("test", limit: 1), Settings.Json)!;
        Assert.Equal("42", output["emails"]![0]!["message_uid"]!.ToString()); Assert.True(output["has_more"]!.GetValue<bool>());
        await connection.Received(1).Open("INBOX", false, Arg.Any<CancellationToken>());
        await box.Received(1).FetchAsync(Arg.Any<IList<UniqueId>>(), Arg.Is<IFetchRequest>(r => !r.Items.HasFlag(MessageSummaryItems.BodyStructure)), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(box.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("Store"));
    }
    [Fact]
    public async Task StaleValidityNeverStoresOrFetches()
    {
        await Assert.ThrowsAsync<MailError>(() => mail.MarkRead("test", "INBOX", "42", "76", default));
        Assert.DoesNotContain(box.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("Store") || c.GetMethodInfo().Name.Contains("Fetch"));
    }
    [Fact]
    public async Task MarkReadOnlyAddsSeenAndVerifies()
    {
        summary.Flags.Returns(MessageFlags.Seen); await mail.MarkRead("test", "INBOX", "42", "77", default);
        await connection.Received(1).Open("INBOX", true, Arg.Any<CancellationToken>());
        var store = Assert.Single(box.ReceivedCalls(), c => c.GetMethodInfo().Name == "StoreAsync");
        var request = Assert.IsAssignableFrom<IStoreFlagsRequest>(store.GetArguments()[1]); Assert.Equal(MessageFlags.Seen, request.Flags); Assert.Equal(StoreAction.Add, request.Action);
    }
    [Fact] public async Task UnconfirmedFlagIsError() { await Assert.ThrowsAsync<MailError>(() => mail.MarkRead("test", "INBOX", "42", "77", default)); }
    [Fact]
    public async Task OversizeMessageDoesNotFetchBody()
    {
        summary.Size.Returns((uint)MailService.MaxMessage + 1); await Assert.ThrowsAsync<MailError>(() => mail.Read("test", "INBOX", "42", "77", default));
        await box.Received(1).FetchAsync(Arg.Any<IList<UniqueId>>(), Arg.Any<IFetchRequest>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(box.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("GetStream"));
    }
    [Theory]
    [InlineData("1:*")]
    [InlineData("0")]
    [InlineData("4294967296")]
    [InlineData("42\r\nSTORE")]
    public async Task InvalidUidNeverConnects(string uid) { await Assert.ThrowsAsync<MailError>(() => mail.MarkRead("test", "INBOX", uid, "77", default)); Assert.Empty(connection.ReceivedCalls()); }
    [Fact] public async Task InvalidSearchDateRejectedBeforeConnect() { await Assert.ThrowsAsync<MailError>(() => mail.List("test", since: "ieri")); Assert.Empty(connection.ReceivedCalls()); }
    private static BodyPartText Part(string type, string encoding, string text) => new(new ContentType("text", type) { Charset = "utf-8" }, "1") { ContentTransferEncoding = encoding, Octets = (uint)Encoding.UTF8.GetByteCount(text), PartSpecifier = "1" };
    [Fact]
    public void PlainPreferredAttachmentsAndEmbeddedMessagesExcluded()
    {
        var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), ""); var plain = Part("plain", "7bit", "Ciao"); body.BodyParts.Add(plain); body.BodyParts.Add(Part("html", "7bit", "<p>Ciao</p>"));
        body.BodyParts.Add(new BodyPartText(new ContentType("text", "plain"), "3") { ContentDisposition = new ContentDisposition("attachment"), Octets = 999999 });
        body.BodyParts.Add(new BodyPartMessage(new ContentType("message", "rfc822"), "4")); Assert.Equal([plain], MailService.ChooseText(body));
    }
    [Fact] public void AttachedMultipartNotTraversed() { var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), "") { ContentDisposition = new ContentDisposition("attachment") }; body.BodyParts.Add(Part("plain", "7bit", "Secret")); Assert.Empty(MailService.ChooseText(body)); }
    [Fact] public void OversizeTextRejectedBeforeFetch() { var body = Part("plain", "7bit", "x"); body.Octets = MailService.MaxText + 1; Assert.Throws<MailError>(() => MailService.ChooseText(body)); }
    [Fact]
    public void Base64AndHtmlDecode()
    {
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("Città")); Assert.Equal("Città", MailService.Decode(Part("plain", "base64", text), Encoding.ASCII.GetBytes(text)));
        var html = "<p>Citt=C3=A0 &amp; grazie</p><script>segreto</script>"; var decoded = MailService.Decode(Part("html", "quoted-printable", html), Encoding.ASCII.GetBytes(html)); Assert.Contains("Città & grazie", decoded); Assert.DoesNotContain("segreto", decoded);
    }
    [Fact]
    public async Task PartialPeekReadNeverDownloadsAttachmentsOrWrites()
    {
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("Ciao è una prova")); var part = Part("plain", "base64", text); var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), ""); body.BodyParts.Add(part);
        body.BodyParts.Add(new BodyPartBasic(new ContentType("application", "pdf"), "2") { ContentDisposition = new ContentDisposition("attachment") { FileName = "test.pdf" }, Octets = 2000, PartSpecifier = "2" }); summary.Body.Returns(body);
        box.GetStreamAsync(new UniqueId(42), "1", 0, (int)part.Octets + 1, Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>()).Returns(new MemoryStream(Encoding.ASCII.GetBytes(text)));
        var output = JsonSerializer.SerializeToNode(await mail.Read("test", "INBOX", "42", "77", default), Settings.Json)!; Assert.Equal("Ciao è una prova", output["text"]!.ToString()); Assert.Single(output["attachments"]!.AsArray());
        await connection.Received(1).Open("INBOX", false, Arg.Any<CancellationToken>()); Assert.DoesNotContain(box.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("Store"));
        Assert.Single(box.ReceivedCalls(), c => c.GetMethodInfo().Name == "GetStreamAsync");
    }
}
