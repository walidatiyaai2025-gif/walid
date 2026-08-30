using PCCExecutive.Application;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerPlanJsonEnvelopeTests
{
    private const string Plan = "{\"ManagerEstimate\":25,\"Tasks\":[]}";

    [Theory]
    [InlineData(Plan)]
    [InlineData("```json\n" + Plan + "\n```")]
    [InlineData("Manager plan follows.\n" + Plan + "\nEnd of plan.")]
    [InlineData("(" + Plan + ")")]
    public void Parser_accepts_exactly_one_structured_plan_inside_harmless_presentation_wrappers(string response)
    {
        var parsed = new StructuredManagerPlanParser().Parse(response);

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}")));
        Assert.NotNull(parsed.Plan);
        Assert.Equal(25m, parsed.Plan!.ManagerEstimate.Percent);
        Assert.Empty(parsed.Plan.Tasks);
    }

    [Fact]
    public void Parser_fails_closed_when_response_contains_two_top_level_plan_objects()
    {
        var response = Plan + "\n" + "{\"ManagerEstimate\":30,\"Tasks\":[]}";

        var parsed = new StructuredManagerPlanParser().Parse(response);

        Assert.False(parsed.IsValid);
        var finding = Assert.Single(parsed.Findings);
        Assert.Equal("MANAGER_PLAN_NOT_STRUCTURED", finding.Code);
        Assert.Contains("multiple top-level JSON objects", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Manager plan is not JSON at all.")]
    [InlineData("Manager plan: {\"ManagerEstimate\":25,\"Tasks\":[}")]
    public void Parser_still_rejects_missing_or_malformed_structured_plan(string response)
    {
        var parsed = new StructuredManagerPlanParser().Parse(response);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Findings, x => x.Code == "MANAGER_PLAN_NOT_STRUCTURED");
    }
}
