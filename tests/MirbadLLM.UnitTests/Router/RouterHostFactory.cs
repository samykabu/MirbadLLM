using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;

namespace MirbadLLM.UnitTests.Router;

/// <summary>Builds and starts a minimal <see cref="WebApplication"/>, backed by the ASP.NET Core
/// <c>TestServer</c>, hosting only <c>AddMirbadLLMRouter</c> / <c>MapMirbadLLMRouter</c> wired to
/// hand-written fakes. Callers get the in-process test client via <c>WebApplication.GetTestClient()</c>.</summary>
internal static class RouterHostFactory
{
    public static async Task<WebApplication> CreateAsync(
        FakeModelEndpointResolver resolver,
        FakeModelRuntime runtime,
        Action<MirbadLLMOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null,
        FakeApiKeyStore? apiKeyStore = null,
        Action<AuthOptions>? configureAuthOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddMirbadLLMRouter();
        builder.Services.AddSingleton<IModelEndpointResolver>(resolver);
        builder.Services.AddSingleton<IModelRuntime>(runtime);
        builder.Services.AddSingleton<IApiKeyStore>(apiKeyStore ?? new FakeApiKeyStore());
        builder.Services.Configure<MirbadLLMOptions>(options => configureOptions?.Invoke(options));
        builder.Services.Configure<AuthOptions>(options => configureAuthOptions?.Invoke(options));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapMirbadLLMRouter();
        await app.StartAsync();
        return app;
    }
}
