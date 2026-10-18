namespace ArubaMail;

/// <summary>Caps total wire bytes per IMAP connection, including search and MIME metadata.</summary>
public sealed class BudgetStream(Stream inner, long budget = 1024 * 1024) : Stream
{
    private long remaining = budget;
    private int Allow(int requested) => remaining > 0 ? (int)Math.Min(requested, remaining) : throw new MailError("Risposta IMAP oltre il limite: restringere ricerca o numero messaggi.");
    private int Count(int read) { remaining -= read; return read; }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allow(count)));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allow(buffer.Length)]));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer[..Allow(buffer.Length)], ct));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override bool CanTimeout => inner.CanTimeout;
    public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
    public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
