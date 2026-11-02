using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>One tool-call case: the prompt, the tool it must call, and every tool offered (each with a JSON schema).</summary>
public sealed record ToolCase(string Prompt, string ExpectedTool);

/// <summary>
/// The fixed inputs of the workload presets (schema v2). Deterministic text; uniqueness comes from per-request
/// suffixes (short chat), per-repetition random prefixes (long prompts) and a per-run tag (shared prefix).
/// </summary>
public static class WorkloadPrompts
{
    /// <summary>Short-chat prompt sets: code, tool-style planning and prose. Request <c>i</c> uses set <c>i % 3</c>.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> ChatSets =
    [
        [
            "Write a Python function that merges overlapping intervals and explain its complexity.",
            "Implement a thread-safe LRU cache in C# and walk through the locking strategy.",
            "Write a TypeScript debounce helper with cancellation and show how to test it.",
            "Refactor a nested loop that finds duplicate rows in a CSV into an efficient SQL query, with explanation.",
            "Write a Go HTTP middleware that adds request ids and structured logging.",
            "Explain and implement Dijkstra's algorithm in Rust with a binary heap.",
            "Write a Bash script that rotates log files older than seven days and compresses them.",
            "Implement a JSON schema validator for a tiny subset of the spec in JavaScript.",
        ],
        [
            "Plan the steps an agent should take to book a flight, a hotel and a car, listing each tool call and its JSON arguments.",
            "As an operations agent, describe the tool calls you would make to diagnose a failing Kubernetes deployment.",
            "List the API calls, with JSON bodies, an assistant needs to create a GitHub issue, label it and assign it.",
            "Describe how an agent would reconcile two calendars, naming the tools it calls and what each returns.",
            "Plan a data pipeline run: which tools fetch, clean, validate and load the data, with their arguments.",
            "Outline the tool calls an email assistant makes to find, summarize and reply to unread customer emails.",
            "Explain the sequence of function calls a coding agent uses to run tests, read failures and patch code.",
            "Describe the tool calls needed to monitor a website, alert on downtime and open an incident ticket.",
        ],
        [
            "Write a long story about a lighthouse keeper who receives letters from the future.",
            "Write a detailed travel essay about a week walking the coast of Oman.",
            "Write a long story about a clockmaker repairing time in a city where it stopped.",
            "Write an in-depth explainer on how bread rises, for curious teenagers.",
            "Write a long story about a gardener on a generation ship.",
            "Write a reflective essay about learning a musical instrument as an adult.",
            "Write a long story about a cartographer mapping a desert that changes every night.",
            "Write a detailed history of the printing press for a museum audio guide.",
        ],
    ];

    /// <summary>Request <paramref name="index"/> of a level: a prompt from one of the three sets plus a distinct suffix.</summary>
    public static string ChatPrompt(int index, string tag)
    {
        var set = ChatSets[index % ChatSets.Count];
        return string.Create(CultureInfo.InvariantCulture, $"{set[(index / ChatSets.Count) % set.Count]} (Variant {index + 1}, request {tag}-{index}.)");
    }

    /// <summary>
    /// A cold long prompt: a unique random token sequence FIRST (so no part of it can come from the prefix cache),
    /// then the deterministic sensor log of <see cref="BenchmarkPrompts.LongPrompt"/>.
    /// </summary>
    public static string ColdLongPrompt(Random random, int lines)
    {
        ArgumentNullException.ThrowIfNull(random);
        return BenchmarkPrompts.LongPrompt(RandomWords(random, 24), lines);
    }

    /// <summary><paramref name="count"/> random 5-letter words (each a few tokens): a sequence no other request shares.</summary>
    public static string RandomWords(Random random, int count)
    {
        ArgumentNullException.ThrowIfNull(random);
        const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789";
        var text = new StringBuilder(count * 6);
        for (int w = 0; w < count; w++)
        {
            if (w > 0)
            {
                text.Append(' ');
            }

            for (int c = 0; c < 5; c++)
            {
                text.Append(Letters[random.Next(Letters.Length)]);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The shared system prompt: a per-run <paramref name="tag"/>, then a deterministic company handbook of
    /// <paramref name="lines"/> numbered policies.
    /// </summary>
    public static string SharedSystemPrompt(string tag, int lines)
    {
        var text = new StringBuilder(lines * 90);
        text.Append("Handbook revision ").Append(tag).Append(". You are the support assistant of Wadi Logistics. Answer from these policies only.\n\n");
        for (int i = 1; i <= lines; i++)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Policy {i:D5}: shipments to zone {i % 37} over {i * 13 % 900 + 100} kg need form W-{i % 211} and a {i % 7 + 1}-day notice.\n");
        }

        return text.ToString();
    }

    public static readonly IReadOnlyList<string> SharedPrefixQuestions =
    [
        "Which form does a shipment under policy 00042 need? Answer briefly.",
        "How many days of notice does policy 00317 require? Answer briefly.",
        "What zone does policy 01234 apply to? Answer briefly.",
        "Summarize policy 00777 in one sentence.",
        "What weight threshold does policy 00500 set? Answer briefly.",
        "Which policy mentions zone 12 first? Answer briefly.",
        "What is the notice period in policy 00090? Answer briefly.",
        "Which form number appears in policy 00211? Answer briefly.",
    ];

    /// <summary>The tools offered in every tool-call case: one schema without properties (the VS Code case) and three with required arguments.</summary>
    public static readonly string ToolsJson = """
        [
          {"type":"function","function":{"name":"lookup","description":"look something up","parameters":{"type":"object"}}},
          {"type":"function","function":{"name":"get_time","description":"Current time in a city",
            "parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}},
          {"type":"function","function":{"name":"get_weather","description":"Weather in a city",
            "parameters":{"type":"object","properties":{"city":{"type":"string"},"unit":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["city","unit"]}}},
          {"type":"function","function":{"name":"create_event","description":"Create a calendar event",
            "parameters":{"type":"object","properties":{"title":{"type":"string"},"attendees":{"type":"array","items":{"type":"string"}},"duration_minutes":{"type":"integer"}},"required":["title","attendees","duration_minutes"]}}}
        ]
        """;

    public static readonly IReadOnlyList<ToolCase> ToolCases =
    [
        new("What time is it in Riyadh? Use a tool.", "get_time"),
        new("What's the weather in Tokyo, in celsius? Use a tool.", "get_weather"),
        new("Schedule a 30-minute meeting titled \"Design review\" with Sara and Omar. Use a tool.", "create_event"),
    ];

    /// <summary>The JSON schema of <paramref name="tool"/>'s parameters from <see cref="ToolsJson"/>.</summary>
    public static JsonElement ToolSchema(string tool)
    {
        using var doc = JsonDocument.Parse(ToolsJson);
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var function = t.GetProperty("function");
            if (function.GetProperty("name").GetString() == tool)
            {
                return function.GetProperty("parameters").Clone();
            }
        }

        throw new ArgumentException($"Unknown tool '{tool}'.", nameof(tool));
    }
}
