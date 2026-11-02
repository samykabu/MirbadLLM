namespace MirbadLLM.UnitTests.Orchestration.Fakes;

/// <summary>Hand-written fake <see cref="HttpMessageHandler"/>. Tests plug in <see cref="Handler"/> and can
/// inspect every captured request/body afterwards.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return Handler(request);
    }
}
