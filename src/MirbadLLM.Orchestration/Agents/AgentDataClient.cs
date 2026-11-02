using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// The data channel of one node for one handler generation (design §7.2): the inference proxy over the node's data
/// handler, HTTP/2 exact and the protocol header on every request (<see cref="GenerationHandler"/>), unbuffered responses,
/// and the per-node in-flight bound (default 512): an excess request gets a local 503 <c>server_busy</c> in the agent error
/// shape with <c>Retry-After: 5</c>, translated by the router like the agent's own (<see cref="ErrorMappings.TranslateProxyError"/>).
/// </summary>
internal sealed class AgentDataClient : IAgentDataClient
{
    private readonly AgentHandlerGeneration _generation;
    private readonly AgentClientContext _context;
    private readonly InFlightLimiter _limiter;

    public AgentDataClient(AgentHandlerGeneration generation, AgentClientContext context, InFlightLimiter limiter)
    {
        _generation = generation;
        _context = context;
        _limiter = limiter;
    }

    public string NodeId => _generation.Endpoint.NodeId;

    public HttpMessageInvoker Invoker => _generation.Data;

    public Uri BuildProxyUri(string modelId, string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        int question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        string path = (question < 0 ? pathAndQuery : pathAndQuery[..question]).TrimStart('/');
        string query = question < 0 ? "" : pathAndQuery[question..];
        return _generation.Endpoint.Resolve(AgentRoutes.Proxy.Expand(modelId, path) + query);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_limiter.TryEnter())
        {
            return Busy(request);
        }

        ValidationTracker tracker = _generation.DataTracker;
        long rejections = tracker.Rejected;
        HttpResponseMessage response;
        try
        {
            response = await _generation.Data.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _limiter.Exit();
            throw AgentErrorMapper.Classify(NodeId, ex, tracker, rejections);
        }
        catch (ObjectDisposedException ex)
        {
            _limiter.Exit();
            throw new AgentUnreachableException(NodeId, $"The data client of node {NodeId} belongs to retired handlers; get a new client from the factory.", ex);
        }
        catch
        {
            _limiter.Exit();
            throw;
        }

        response.Content = new ReleasingContent(response.Content, _limiter.Exit);
        return response;
    }

    private HttpResponseMessage Busy(HttpRequestMessage request)
    {
        var body = new AgentErrorResponse(new AgentError
        {
            Code = ErrorCodes.ServerBusy,
            Message = $"Too many requests in flight to node {NodeId} ({_context.Options.MaxDataInFlightPerNode}).",
        });
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, AgentJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue(AgentJson.JsonContentType);
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = content,
            RequestMessage = request,
            Version = HttpVersion.Version20,
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        response.Headers.TryAddWithoutValidation(Protocol.AgentErrorHeader, ErrorCodes.ServerBusy);
        return response;
    }
}
