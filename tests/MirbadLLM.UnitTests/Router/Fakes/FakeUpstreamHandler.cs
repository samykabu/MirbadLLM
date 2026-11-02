namespace MirbadLLM.UnitTests.Router.Fakes;

/// <summary>Fake primary <see cref="HttpMessageHandler"/> for the router's upstream <see cref="HttpClient"/>.
/// Tests plug in <see cref="Handler"/> to control the response, throw to simulate an upstream failure, or
/// inspect <see cref="LastRequest"/>.</summary>
internal sealed class FakeUpstreamHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; }
        = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Handler(request, cancellationToken);
    }
}
