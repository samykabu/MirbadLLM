namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// A forward-only read wrapper over the client's request body that fails once more than the limit was read. The body
/// still streams through (nothing is buffered here); the limit holds even where the server's own per-request limit
/// cannot be set (body already being read, a test server).
/// </summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        return _read > limit ? throw new ProxyBodyTooLargeException(limit) : read;
    }
}

/// <summary>The proxied request body exceeded <see cref="AgentProxyOptions.MaxRequestBodyBytes"/>.</summary>
internal sealed class ProxyBodyTooLargeException : IOException
{
    public ProxyBodyTooLargeException()
        : base("The request body exceeds the proxy limit.")
    {
    }

    public ProxyBodyTooLargeException(string message)
        : base(message)
    {
    }

    public ProxyBodyTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProxyBodyTooLargeException(long limit)
        : base($"The request body exceeds the {limit} byte proxy limit.")
    {
    }
}
