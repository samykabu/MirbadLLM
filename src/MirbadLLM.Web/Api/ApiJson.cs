using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MirbadLLM.Web.Api;

/// <summary>
/// The JSON settings of every minimal-API endpoint (admin API, <c>/api/status</c>, <c>/v1</c> helpers), in one place so
/// Program.cs and the test hosts cannot drift apart: enums as their names ("Ready" reads better than "2"), and a
/// non-finite double written as <c>null</c>.
/// </summary>
public static class ApiJson
{
    /// <summary>Applies <see cref="Configure"/> to the HTTP JSON options.</summary>
    public static IServiceCollection ConfigureApiJson(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options => Configure(options.SerializerOptions));

    /// <summary>Adds the API's converters to <paramref name="options"/>.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new NonFiniteDoubleAsNullConverter());
    }
}

/// <summary>
/// Writes NaN and ±infinity as <c>null</c> and reads <c>null</c> back as NaN. In-process, NaN means "no source for this
/// number" (a llama.cpp model has no KV-cache gauge and no request counter, <c>ModelKpiCalculator</c>), and the UI shows
/// it as "—"; System.Text.Json refuses to write it and failed the whole response half-way, so <c>/api/status</c> answered
/// 500 with a truncated body as soon as a llama.cpp model was Ready (MN-241, 2026-10-15).
/// </summary>
public sealed class NonFiniteDoubleAsNullConverter : JsonConverter<double>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
