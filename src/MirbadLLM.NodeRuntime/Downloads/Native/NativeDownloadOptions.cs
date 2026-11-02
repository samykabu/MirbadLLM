namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// Settings of the native downloader (design §6.4 "Native downloader", design-p3p4 §A.2.1, §A.8 "Downloads"). The defaults
/// are the production rules; tests and the local dev setup (design §13 item 5, a fake Hugging Face endpoint on loopback)
/// change <see cref="AllowLoopbackHttp"/> and the timings.
/// </summary>
public sealed class NativeDownloadOptions
{
    /// <summary>
    /// Allows plain <c>http://</c> to a loopback host (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>). Default false:
    /// HTTPS only, for the endpoint and for every redirect (a redirect to loopback could otherwise reach a local service).
    /// </summary>
    public bool AllowLoopbackHttp { get; set; }

    /// <summary>Most redirects followed for one request (Hugging Face sends one, to its CDN).</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Attempts per file for transient failures (connection reset, 5xx, 429, a body cut short); each resumes by range.</summary>
    public int MaxAttemptsPerFile { get; set; } = 3;

    /// <summary>Wait between transient attempts.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A response body that delivers nothing for this long is abandoned (and resumed as a transient failure).</summary>
    public TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Copy buffer size.</summary>
    public int BufferSize { get; set; } = 256 * 1024;

    /// <summary>Largest Hugging Face API listing read (a hostile or broken endpoint must not make the agent allocate gigabytes).</summary>
    public long MaxListingBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Most files one download may have (bounds the manifest and the listing).</summary>
    public int MaxFiles { get; set; } = 10_000;
}
