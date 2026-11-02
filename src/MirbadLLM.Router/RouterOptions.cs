using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Router;

/// <summary>Router limits, bound from <c>MirbadLLM:Router</c> (design §7.2 "Bounded resources").</summary>
public sealed class RouterOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Router";

    /// <summary>
    /// Global in-flight upload budget: the request bytes all <c>/v1</c> requests may hold buffered at once (default
    /// 512 MiB). A request that cannot get its share within <see cref="UploadBudgetWait"/> gets 503 <c>server_busy</c>.
    /// </summary>
    public long MaxBufferedBodyBytes { get; set; } = Protocol.DefaultMaxBufferedBodyBytes;

    /// <summary>How long a request waits for upload budget before 503 <c>server_busy</c> (default 10 s).</summary>
    public TimeSpan UploadBudgetWait { get; set; } = Protocol.UploadBudgetWait;
}
