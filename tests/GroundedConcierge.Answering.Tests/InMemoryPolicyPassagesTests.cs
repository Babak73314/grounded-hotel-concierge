using GroundedConcierge.Answering;

namespace GroundedConcierge.Answering.Tests;

public class InMemoryPolicyPassagesTests
{
    private readonly InMemoryPolicyPassages _passages = new();

    [Theory]
    [InlineData("What time is breakfast?", false, "breakfast")]
    [InlineData("Kahvaltı saat kaçta?", true, "breakfast")]
    [InlineData("Is there parking for my car?", false, "parking")]
    [InlineData("Otopark var mı?", true, "parking")]
    [InlineData("Can I bring my dog?", false, "pets")]
    public void AQuestionFindsItsPassageFirst(string question, bool turkish, string expectedId)
    {
        var found = _passages.Find(question, turkish);
        Assert.Equal(expectedId, found[0].Id);
    }

    [Theory]
    [InlineData("Do you have a helipad?")]
    [InlineData("")]
    [InlineData("   ")]
    public void AQuestionNoPassageCoversFindsNothing(string question) =>
        Assert.Empty(_passages.Find(question, turkish: false));

    [Fact]
    public void ThePassageComesInTheGuestsLanguage()
    {
        Assert.StartsWith("Breakfast", _passages.Find("breakfast", turkish: false)[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Kahvaltı", _passages.Find("kahvaltı", turkish: true)[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultIsBounded() =>
        Assert.True(_passages.Find("breakfast parking dog check-in", turkish: false, max: 2).Count <= 2);
}
