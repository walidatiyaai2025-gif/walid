using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class ChatGptCompletionRegressionTests
{
    [Fact]
    public void Complete_assistant_response_does_not_require_visible_response_action_controls()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.ResponseCompleteWithoutActions);

        Assert.Equal(GenerationState.Complete, snapshot.Generation.State);
        Assert.Equal(ResponseCompleteness.Complete, snapshot.ResponseCompleteness);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CapturedResponseText));

        var decision = new ChatGptResilienceClassifier().Classify(snapshot, TimeSpan.FromSeconds(3));
        Assert.Equal(ChatGptResilienceState.Done, decision.State);
    }

    [Fact]
    public void Active_generation_and_continue_controls_remain_non_complete()
    {
        var generating = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.Generating);
        var partial = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.PartialResponse);

        Assert.Equal(GenerationState.Generating, generating.Generation.State);
        Assert.NotEqual(ResponseCompleteness.Complete, generating.ResponseCompleteness);
        Assert.Equal(ResponseCompleteness.Partial, partial.ResponseCompleteness);
    }
    [Fact]
    public void Modern_conversation_turn_markup_is_detected_as_completed_assistant_response()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.ResponseCompleteModernTurn);

        Assert.Equal(1, snapshot.AssistantMessageCount);
        Assert.Equal(GenerationState.Complete, snapshot.Generation.State);
        Assert.Equal(ResponseCompleteness.Complete, snapshot.ResponseCompleteness);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CapturedResponseText));
    }

}
