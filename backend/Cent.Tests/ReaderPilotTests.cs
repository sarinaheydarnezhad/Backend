using Cent.Module;
namespace Cent.Tests;
public class ReaderPilotTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"pdf\",\"x\":2,\"y\":0.5}")]
    [InlineData("{\"kind\":\"html\",\"x\":0.5,\"y\":0.5}")]
    public void Invalid_anchors_are_rejected(string value) => Assert.Throws<CentException>(() => CentReaderPilot.ValidateAnchor(value));
    [Fact]
    public void Legacy_and_normalized_anchors_are_accepted()
    {
        CentReaderPilot.ValidateAnchor(null);
        CentReaderPilot.ValidateAnchor("{\"kind\":\"html\",\"block\":\"section:block\",\"quote\":\"text\",\"x\":0.25,\"y\":0.75}");
        CentReaderPilot.ValidateAnchor("{\"kind\":\"pdf\",\"x\":0.25,\"y\":0.75}");
    }
    [Fact]
    public void Original_lesson_contains_all_examples_and_answers_only()
    {
        var html = CentReaderPilot.Html();
        Assert.Contains("مثال ۳", html); Assert.Contains("۱۱۶/۹", html);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<details>").Count);
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("id=\"s2\"", html);
    }
}
