using DocaDesk.Core;

namespace DocaDesk.Tests;

public class PanelAttentionTests
{
    [Theory]
    [InlineData("""{"type":"doca.attention","kinds":[]}""", 0)]
    [InlineData("""{"type":"doca.attention","kinds":["password"]}""", 1)]
    [InlineData("""{"type":"doca.attention","kinds":["question","approval"]}""", 2)]
    public void Reads_the_panels_message(string json, int count) => Assert.Equal(count, PanelAttention.Parse(json)!.Count);

    [Theory]
    [InlineData(null)]
    [InlineData("\"doca.attention\"")]
    [InlineData("""{"type":"other","kinds":["password"]}""")]
    [InlineData("""{"type":"doca.attention","kinds":["password","hunter2"]}""")]
    [InlineData("""{"type":"doca.attention","kinds":"password"}""")]
    [InlineData("""{"type":"doca.attention","kinds":["a","b","c","d"]}""")]
    [InlineData("not json")]
    public void Anything_else_changes_nothing(string? json) => Assert.Null(PanelAttention.Parse(json));

    [Fact]
    public void A_dialog_in_a_hidden_window_is_told_once_and_marked_until_answered()
    {
        var a = new PanelAttention();
        var first = a.Update([AttentionKind.Password], inFront: false);
        Assert.Equal("DOCA needs your password", first.ToastTitle);
        Assert.True(first.Badge);

        var again = a.Update([AttentionKind.Password], inFront: false);   // the page re-posting the same set
        Assert.Null(again.ToastTitle);
        Assert.True(again.Badge);

        var approval = a.Update([AttentionKind.Password, AttentionKind.Approval], inFront: false);
        Assert.Equal("The agent is asking to do something", approval.ToastTitle);

        var answered = a.Update([], inFront: false);
        Assert.Null(answered.ToastTitle);
        Assert.False(answered.Badge);
        Assert.True(answered.ClearToast);

        Assert.NotNull(a.Update([AttentionKind.Password], inFront: false).ToastTitle);   // asked again: told again
    }

    [Fact]
    public void Nothing_is_told_while_the_person_looks_at_the_panel()
    {
        var a = new PanelAttention();
        var seen = a.Update([AttentionKind.Question], inFront: true);
        Assert.Null(seen.ToastTitle);
        Assert.False(seen.Badge);

        // Sent to the tray with it still open: the mark comes back, the notification does not.
        Assert.True(a.BadgeWhenHidden);
        Assert.Null(a.Update([AttentionKind.Question], inFront: false).ToastTitle);
    }

    [Fact]
    public void Bringing_the_window_forward_takes_the_notification_back()
    {
        var a = new PanelAttention();
        a.Update([AttentionKind.Approval], inFront: false);
        var shown = a.Shown();
        Assert.True(shown.ClearToast);
        Assert.False(shown.Badge);
        Assert.Null(a.Update([AttentionKind.Approval], inFront: false).ToastTitle);
    }

    [Fact]
    public void The_watcher_reads_which_dialogs_are_open_never_what_they_hold()
    {
        foreach (var reads in new[] { ".value", "textContent", "innerText", "innerHTML", "outerHTML" })
            Assert.DoesNotContain(reads, PanelAttention.Script);
        Assert.Contains("window !== window.top", PanelAttention.Script);
    }

    [Fact]
    public void The_words_never_come_from_the_page()
    {
        foreach (var k in Enum.GetValues<AttentionKind>())
        {
            var (title, text) = PanelAttention.Words(k);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }
}
