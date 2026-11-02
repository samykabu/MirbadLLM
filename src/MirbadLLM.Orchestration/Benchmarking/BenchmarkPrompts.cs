using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>A question with one objective answer, scored by <see cref="Pattern"/> against the reply.</summary>
public sealed record QaQuestion(string Question, string Expected, Regex Pattern);

/// <summary>
/// The fixed inputs of the benchmark suite, embedded so every run on every box sends byte-identical requests.
/// </summary>
public static partial class BenchmarkPrompts
{
    /// <summary>Generation prompts; request <c>i</c> of a level uses prompt <c>i % Count</c>.</summary>
    public static readonly IReadOnlyList<string> Stories =
    [
        "Write a long story about robot #1, a lighthouse keeper on a stormy coast.",
        "Write a long story about robot #2, who repairs clocks in an old city.",
        "Write a long story about robot #3, a gardener on a space station.",
        "Write a long story about robot #4, who delivers mail across a desert.",
        "Write a long story about robot #5, a cook in a busy night market.",
        "Write a long story about robot #6, who maps caves under a mountain.",
        "Write a long story about robot #7, a librarian in a flooded town.",
        "Write a long story about robot #8, who trains racing pigeons.",
        "Write a long story about robot #9, a tailor for travelling actors.",
        "Write a long story about robot #10, who guards a seed vault in the Arctic.",
        "Write a long story about robot #11, a ferry pilot on a wide river.",
        "Write a long story about robot #12, who restores stained glass windows.",
        "Write a long story about robot #13, a beekeeper on a hillside farm.",
        "Write a long story about robot #14, who runs a tiny radio station.",
        "Write a long story about robot #15, a mountain rescue climber.",
        "Write a long story about robot #16, who builds violins by hand.",
    ];

    /// <summary>Objective questions with one right answer each (arithmetic, a unit conversion, code outputs, facts).</summary>
    public static readonly IReadOnlyList<QaQuestion> Questions =
    [
        new("What is 17 multiplied by 23? Reply with only the number.", "391", Answer391()),
        new("What is 1024 divided by 16? Reply with only the number.", "64", Answer64()),
        new("How many centimeters are in 3.5 meters? Reply with only the number.", "350", Answer350()),
        new("What does this Python code print?\n\nprint(len('mirbadllm'))\n\nReply with only the output.", "11", Answer11()),
        new("What does this Python code print?\n\nprint(sum(range(5)))\n\nReply with only the output.", "10", Answer10()),
        new("What does this JavaScript code print?\n\nconsole.log([1, 2, 3].map(x => x * 2).join(','))\n\nReply with only the output.", "2,4,6", Answer246()),
        new("What is the chemical symbol for gold? Reply with only the symbol.", "Au", AnswerAu()),
        new("What is the capital city of Australia? Reply with only the city name.", "Canberra", AnswerCanberra()),
    ];

    public const string ToolCallPrompt = "What time is it in Riyadh? Use a tool.";

    /// <summary>
    /// Same tools as the Phase 0 bench: one function whose parameters schema has no <c>properties</c> (the case
    /// VS Code clients send), and one with a required string argument.
    /// </summary>
    public static object ToolDefinitions { get; } = new object[]
    {
        new
        {
            type = "function",
            function = new { name = "get_weather", description = "get weather", parameters = new { type = "object" } },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_time",
                description = "get time",
                parameters = new
                {
                    type = "object",
                    properties = new { city = new { type = "string" } },
                    required = new[] { "city" },
                },
            },
        },
    };

    public static readonly IReadOnlyList<string> ToolNames = ["get_weather", "get_time"];

    /// <summary>
    /// A long, deterministic filler document of <paramref name="lines"/> numbered records, preceded by a
    /// <paramref name="uniquePrefix"/> so the first request cannot hit the prefix cache.
    /// </summary>
    public static string LongPrompt(string uniquePrefix, int lines)
    {
        var text = new StringBuilder(lines * 80);
        text.Append("Session ").Append(uniquePrefix).Append(".\n");
        text.Append("Below is a sensor log. Read it, then answer the question at the end.\n\n");
        for (int i = 1; i <= lines; i++)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Record {i:D6}: station {i % 97} reported value {i * 7919 % 10007} at step {i}.\n");
        }

        text.Append("\nQuestion: how many records are in the log? Reply with only the number.");
        return text.ToString();
    }

    /// <summary>Lines in <see cref="LongPrompt"/> used to calibrate tokens per line.</summary>
    public const int CalibrationLines = 200;

    [GeneratedRegex(@"\b391\b", RegexOptions.CultureInvariant)]
    private static partial Regex Answer391();

    [GeneratedRegex(@"\b64\b", RegexOptions.CultureInvariant)]
    private static partial Regex Answer64();

    [GeneratedRegex(@"\b350\b", RegexOptions.CultureInvariant)]
    private static partial Regex Answer350();

    [GeneratedRegex(@"\b11\b", RegexOptions.CultureInvariant)]
    private static partial Regex Answer11();

    [GeneratedRegex(@"\b10\b", RegexOptions.CultureInvariant)]
    private static partial Regex Answer10();

    [GeneratedRegex(@"\b2\s*,\s*4\s*,\s*6\b", RegexOptions.CultureInvariant)]
    private static partial Regex Answer246();

    [GeneratedRegex(@"\bAu\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnswerAu();

    [GeneratedRegex(@"\bCanberra\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AnswerCanberra();
}
