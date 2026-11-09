using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Multinode;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>The read-back of SGLang and NIM containers: what the Apply dialog compares the saved model with.</summary>
public sealed class ContainerRuntimeArgsParserTests
{
    private static readonly ModelDefinition Sglang = new()
    {
        Id = "sg",
        ServedName = "SG",
        ModelPath = "sg-folder",
        Runtime = RuntimeKind.Sglang,
        ToolCallParser = null,
        ReasoningParser = null,
        Container = new ContainerRuntimeSettings { Image = "sglang:1", ExtraArgs = ["--tp", "2", "--speculative-eagle-topk", "4"] },
        Speculative = new SpeculativeDecoding("eagle3", 3) { DraftModel = "/models/drafter" },
    };

    private static LaunchConfig Launched(ModelDefinition model) => new()
    {
        Runtime = model.Runtime,
        Image = model.Container!.Image,
        ImageDigest = model.Container.ImageDigest,
        Argv =
        [
            "python3", "-m", "sglang.launch_server", "--model-path", $"/models/{model.ModelPath}", "--served-model-name", model.ServedName,
            "--host", "0.0.0.0", "--port", "8000",
            .. model.Speculative is { } speculative ? SglangSpeculativeArgs.Build(speculative) : [],
            .. model.Container.ExtraArgs,
        ],
    };

    [Fact]
    public void TheMetricsFlagTheBuilderAdds_IsNotAnExtraArgument_AndDoesNotSwallowTheNextOne()
    {
        var launched = Launched(Sglang);
        string[] argv = [.. launched.Argv.Take(11), "--enable-metrics", .. launched.Argv.Skip(11)];
        var parsed = ContainerRuntimeArgsParser.Parse(launched with { Argv = argv }, Sglang);
        Assert.Equal(Sglang.Container!.ExtraArgs, parsed.Container!.ExtraArgs);
        Assert.Empty(SettingChanges.AllRestartNeeded(parsed, Sglang));

        var typed = Sglang with { Container = Sglang.Container with { ExtraArgs = ["--enable-metrics", "--tp", "2"] } };
        Assert.Equal(["--enable-metrics", "--tp", "2"], ContainerRuntimeArgsParser.Parse(Launched(typed), typed).Container!.ExtraArgs);
    }

    [Fact]
    public void TheTpSizeTheBuilderAdds_IsNotAnExtraArgument_ButOneTheOperatorSetStaysOne()
    {
        var plain = Sglang with { Container = Sglang.Container! with { ExtraArgs = [] } };
        var launched = Launched(plain);
        var added = ContainerRuntimeArgsParser.Parse(launched with { Argv = [.. launched.Argv, "--tp-size", "2"] }, plain);
        Assert.Empty(added.Container!.ExtraArgs);

        var typed = Sglang with { Container = Sglang.Container! with { ExtraArgs = ["--tp-size", "1"] } };
        Assert.Equal(["--tp-size", "1"], ContainerRuntimeArgsParser.Parse(Launched(typed), typed).Container!.ExtraArgs);
    }

    [Fact]
    public void AnUnchangedSglangModel_ShowsNoChanges_ThoughTheAlgorithmIsUpperCasedWhenLaunched()
    {
        var launched = ContainerRuntimeArgsParser.Parse(Launched(Sglang), Sglang);

        Assert.Empty(SettingChanges.AllRestartNeeded(launched, Sglang));
    }

    [Fact]
    public void EachSavedChange_IsListedOnce_AgainstWhatRuns()
    {
        var launched = ContainerRuntimeArgsParser.Parse(Launched(Sglang), Sglang);
        var saved = Sglang with
        {
            ServedName = "SG2",
            Speculative = null,
            Container = Sglang.Container! with { Image = "sglang:2", ExtraArgs = ["--tp", "4"] },
        };

        Assert.Equal(
            ["Served name", "Speculative decoding", "Container image", "Container extra args"],
            SettingChanges.AllRestartNeeded(launched, saved).Select(c => c.Label));
    }

    [Fact]
    public void Nim_ComparesTheImage_ButIgnoresACommandTheImageSetItself()
    {
        var nim = Sglang with { Runtime = RuntimeKind.Nim, Speculative = null, Container = new ContainerRuntimeSettings { Image = "nim:1", ImageDigest = "sha256:a" } };
        var launch = Launched(nim) with { Argv = ["/opt/nim/start.sh"] };

        var launched = ContainerRuntimeArgsParser.Parse(launch, nim);

        Assert.Empty(SettingChanges.AllRestartNeeded(launched, nim));
        Assert.Equal(["Container image"], SettingChanges.AllRestartNeeded(launched, nim with { Container = nim.Container! with { Image = "nim:2" } }).Select(c => c.Label));
    }
}
