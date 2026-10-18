using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace ArubaMail;

public interface IMailConnection : IAsyncDisposable
{
    Task<IMailFolder> Open(string folder, bool write, CancellationToken ct);
    Task<IList<IMailFolder>> Folders(CancellationToken ct);
}
internal sealed class MailConnection(ImapClient client, System.Net.Sockets.TcpClient tcp) : IMailConnection
{
    public static async Task<IMailConnection> Connect(Account account, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 20000 };
        var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            // System TLS validation; never install a certificate bypass or protocol logger.
            await tcp.ConnectAsync(account.ImapHost, account.ImapPort, ct);
            var stream = new BudgetStream(tcp.GetStream());
            await client.ConnectAsync(stream, account.ImapHost, account.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(account.Username, account.Password, ct);
            return new MailConnection(client, tcp);
        }
        catch { client.Dispose(); tcp.Dispose(); throw; }
    }
    public async Task<IMailFolder> Open(string folder, bool write, CancellationToken ct)
    {
        var box = await client.GetFolderAsync(Validate.Imap(folder), ct);
        await box.OpenAsync(write ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, ct); return box;
    }
    public async Task<IList<IMailFolder>> Folders(CancellationToken ct)
    {
        var result = new List<IMailFolder>();
        foreach (var ns in client.PersonalNamespaces) result.AddRange(await client.GetFoldersAsync(ns, false, ct));
        return result.DistinctBy(v => v.FullName).ToList();
    }
    public async ValueTask DisposeAsync()
    {
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await client.DisconnectAsync(true, deadline.Token); }
        catch { }
        finally { client.Dispose(); tcp.Dispose(); } // LOGOUT, never CLOSE/EXPUNGE.
    }
}
public sealed class MailService(Func<string, CancellationToken, Task<IMailConnection>>? connect = null)
{
    public const int MaxMessage = 5 * 1024 * 1024, MaxText = 128 * 1024, MaxHeaders = 16384;
    private Task<IMailConnection> Connect(string account, CancellationToken ct) => connect != null ? connect(account, ct) : MailConnection.Connect(Settings.Account(account), ct);
    public async Task<object> Folders(string account, CancellationToken ct)
    {
        await using var session = await Connect(account, ct);
        return new { account_id = account, folders = (await session.Folders(ct)).Select(f => new { name = f.FullName, selectable = !f.Attributes.HasFlag(FolderAttributes.NoSelect) }).ToArray() };
    }
    private static void Identity(IMailFolder box, string uid, string validity)
    {
        Validate.Uid(uid); Validate.Uid(validity);
        if (box.UidValidity.ToString(CultureInfo.InvariantCulture) != validity) throw new MailError("UIDVALIDITY cambiato: rileggere l’elenco prima di operare.");
    }
    private static JsonRow Header(IMessageSummary s, string account, string folder, uint validity)
    {
        var e = s.Envelope ?? throw new MailError("Intestazioni non disponibili.");
        var values = new[] { e.From.ToString(), e.To.ToString(), e.Cc.ToString(), e.Subject ?? "", e.MessageId ?? "" };
        if (values.Sum(v => Encoding.UTF8.GetByteCount(v)) >= MaxHeaders || s.Size == null) throw new MailError("Intestazioni troppo grandi o dimensione non disponibile.");
        return new JsonRow(account, folder, s.UniqueId.Id.ToString(), validity.ToString(), values[0], values[1], values[2], values[3], e.Date?.ToString("O") ?? "", values[4], s.Flags?.HasFlag(MessageFlags.Seen) ?? false, s.Size.Value);
    }
    private static async Task<IMessageSummary> Summary(IMailFolder box, UniqueId uid, bool body, CancellationToken ct)
    {
        var summaries = await box.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Size | MessageSummaryItems.Envelope | (body ? MessageSummaryItems.BodyStructure : MessageSummaryItems.None), ct);
        return summaries.FirstOrDefault(s => s.UniqueId == uid) ?? throw new MailError("Messaggio non disponibile: potrebbe essere stato spostato o rimosso.");
    }
    public async Task<object> List(string account, string folder = "INBOX", bool unread = false, int limit = 10, int offset = 0, string? sender = null, string? subject = null, string? since = null, string? before = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > 30 || offset is < 0 or > 10000) throw new MailError("Limite 1–30; offset 0–10000.");
        Validate.Imap(folder); var query = unread ? SearchQuery.NotSeen : SearchQuery.All;
        if (!string.IsNullOrEmpty(sender)) query = query.And(SearchQuery.FromContains(Validate.Imap(sender)));
        if (!string.IsNullOrEmpty(subject)) query = query.And(SearchQuery.SubjectContains(Validate.Imap(subject)));
        foreach (var (date, isBefore) in new[] { (since, false), (before, true) })
            if (date != null)
            {
                if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new MailError("Data non valida: usare YYYY-MM-DD.");
                query = query.And(isBefore ? SearchQuery.DeliveredBefore(d) : SearchQuery.DeliveredAfter(d));
            }
        await using var session = await Connect(account, ct); var box = await session.Open(folder, false, ct);
        var ids = await box.SearchAsync(query, ct); var selected = ids.OrderByDescending(v => v.Id).Skip(offset).Take(limit).ToArray();
        var rows = new List<JsonRow>();
        // Fetch only requested UIDs; do not request body, attachments or whole messages.
        foreach (var id in selected) rows.Add(Header(await Summary(box, id, false, ct), account, folder, box.UidValidity));
        return new { emails = rows, matched = ids.Count, offset, has_more = offset + limit < ids.Count, order = "UID descending" };
    }
    public static IEnumerable<BodyPartBasic> Leaves(BodyPart? part, int depth = 0)
    {
        if (depth > 30) throw new MailError("Struttura MIME troppo complessa.");
        if (part is BodyPartMultipart multi)
        {
            if (multi.ContentDisposition?.Disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true) yield break;
            foreach (var child in multi.BodyParts) foreach (var leaf in Leaves(child, depth + 1)) yield return leaf;
        }
        else if (part is BodyPartBasic basic) yield return basic; // Embedded messages stay opaque, never traversed.
    }
    public static BodyPartText[] ChooseText(BodyPart? body)
    {
        var candidates = Leaves(body).OfType<BodyPartText>().Where(v => !v.IsAttachment && string.IsNullOrEmpty(v.FileName) && (v.IsPlain || v.IsHtml)).ToArray();
        var plain = candidates.Where(v => v.IsPlain).ToArray(); var chosen = plain.Length > 0 ? plain : candidates;
        if (chosen.Length > 20 || chosen.Sum(v => (long)v.Octets) > MaxText) throw new MailError("Testo oltre il limite 128 KiB: corpo non scaricato.");
        return chosen;
    }
    public static string Decode(BodyPartText part, byte[] raw)
    {
        var encoding = part.ContentTransferEncoding?.ToLowerInvariant() switch
        {
            "base64" => ContentEncoding.Base64,
            "quoted-printable" => ContentEncoding.QuotedPrintable,
            "7bit" => ContentEncoding.SevenBit,
            "8bit" => ContentEncoding.EightBit,
            "binary" => ContentEncoding.Binary,
            _ => throw new MailError("Codifica MIME non supportata.")
        };
        using var input = new MemoryStream(raw); using var output = new MemoryStream();
        new MimeContent(input, encoding).DecodeTo(output);
        Encoding charset;
        try { charset = Encoding.GetEncoding(part.ContentType.Charset ?? "utf-8"); } catch (ArgumentException) { charset = Encoding.UTF8; }
        var text = charset.GetString(output.ToArray());
        if (!part.IsHtml) return text;
        text = Regex.Replace(text, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"<(?:br|/?p|/?div|/?li|/?tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
    }
    public async Task<object> Read(string account, string folder, string uid, string validity, CancellationToken ct)
    {
        Validate.Imap(folder); Validate.Uid(uid); Validate.Uid(validity);
        await using var session = await Connect(account, ct); var box = await session.Open(folder, false, ct); Identity(box, uid, validity);
        var id = new UniqueId(Validate.Uid(uid)); var summary = await Summary(box, id, false, ct);
        var header = Header(summary, account, folder, box.UidValidity);
        if (summary.Size > MaxMessage) throw new MailError("Messaggio oltre il limite di 5 MiB: corpo non scaricato.");
        summary = await Summary(box, id, true, ct); var chosen = ChooseText(summary.Body); var texts = new List<string>();
        foreach (var part in chosen)
        {
            // Partial BODY.PEEK fetch; even a misleading BODYSTRUCTURE cannot request an unbounded body.
            using var stream = await box.GetStreamAsync(id, part.PartSpecifier, 0, checked((int)part.Octets + 1), ct);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, ct)) > 0)
            {
                if (buffer.Length + count > MaxText || buffer.Length + count > part.Octets) throw new MailError("Dimensione MIME inattesa.");
                buffer.Write(bytes, 0, count);
            }
            if (buffer.Length != part.Octets) throw new MailError("Parte MIME incompleta.");
            texts.Add(Decode(part, buffer.ToArray()));
        }
        var result = System.Text.Json.JsonSerializer.SerializeToNode(header, Settings.Json)!.AsObject();
        result["text"] = string.Join("\n", texts); result["untrusted_content"] = true;
        result["attachments"] = System.Text.Json.JsonSerializer.SerializeToNode(Leaves(summary.Body).Where(p => p is not BodyPartText || p.IsAttachment || !string.IsNullOrEmpty(p.FileName)).Select(p => new { section = p.PartSpecifier, mime_type = p.ContentType.MimeType, size_bytes = p.Octets, filename = p.FileName }));
        return result;
    }
    public async Task<object> MarkRead(string account, string folder, string uid, string validity, CancellationToken ct)
    {
        Validate.Imap(folder); Validate.Uid(uid); Validate.Uid(validity);
        await using var session = await Connect(account, ct); var box = await session.Open(folder, true, ct); Identity(box, uid, validity);
        var id = new UniqueId(Validate.Uid(uid)); await Summary(box, id, false, ct);
        await box.AddFlagsAsync(id, MessageFlags.Seen, true, ct);
        if ((await Summary(box, id, false, ct)).Flags?.HasFlag(MessageFlags.Seen) != true) throw new MailError("Il server non ha confermato il flag letto: rileggere lo stato prima di riprovare.");
        return new { account_id = account, folder, message_uid = uid, uidvalidity = validity, read = true };
    }
}
public sealed record JsonRow(string AccountId, string Folder, string MessageUid, string Uidvalidity, string From, string To, string Cc, string Subject, string Date, string MessageId, bool Read, uint SizeBytes);
