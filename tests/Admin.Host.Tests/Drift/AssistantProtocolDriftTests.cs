using System.Text.Json.Nodes;
using Admin.Host.Tests.Assistant;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The SPA's copy of the assistant protocol's golden files (<c>protocol.ts</c> cites them) against their owner, the
/// <c>Common.Assistant</c> package's under <c>src/BuildingBlocks/Common.Assistant/Protocol/golden/</c>, both ways. A
/// changed golden file on the backend's main fails here before a package carrying it is pinned; the host's
/// <see cref="ProtocolGoldenTests"/> hold the same copy to the version that is.
/// </summary>
public sealed class AssistantProtocolDriftTests
{
    private static readonly string[] Owner = ["src", "BuildingBlocks", "Common.Assistant", "Protocol", "golden"];

    public static TheoryData<string> Copied => [.. Names(ProtocolGoldenTests.GoldenDirectory)];

    [Theory]
    [MemberData(nameof(Copied))]
    public void The_copy_still_reads_as_its_owner_writes_it(string name)
    {
        JsonNode owner = JsonNode.Parse(Backend.Read([.. Owner, name]))!;
        JsonNode copy = JsonNode.Parse(File.ReadAllText(Path.Combine(ProtocolGoldenTests.GoldenDirectory, name)))!;

        JsonNode.DeepEquals(copy, owner).ShouldBeTrue($"{name}: the owner now reads {owner.ToJsonString()}");
    }

    [Fact]
    public void Every_golden_file_of_the_owner_is_copied()
    {
        Names(Path.Combine([Backend.Dir, .. Owner])).ShouldBe(Names(ProtocolGoldenTests.GoldenDirectory));
    }

    private static string[] Names(string directory) =>
        [.. Directory.GetFiles(directory, "*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
}
