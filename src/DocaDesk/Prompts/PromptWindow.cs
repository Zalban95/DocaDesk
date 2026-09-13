using DocaDesk.Core;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DocaDesk.Prompts;

public sealed class PromptWindow : Window
{
    private readonly DocaClient _client;
    private readonly SelectionIdTracker _selection = new();
    private PromptDocument _prompt;
    private readonly StackPanel _blocks = new() { Spacing = 8 };
    private readonly StackPanel _choices = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.WrapWholeWords };
    private readonly ScrollViewer _scroll = new();

    public PromptWindow(PromptDocument prompt, DocaClient client)
    {
        _client = client;
        _prompt = prompt;
        Title = prompt.Title ?? "Prompt";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        var root = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = prompt.Title ?? "Prompt",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(_blocks);
        body.Children.Add(_choices);
        _scroll.Content = body;
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 560));
        Bind(prompt, client);
    }

    public void Bind(PromptDocument prompt, DocaClient client)
    {
        _prompt = prompt;
        Title = prompt.Title ?? "Prompt";
        _blocks.Children.Clear();
        _choices.Children.Clear();
        _status.Text = "";

        if (prompt.Blocks is not null)
        {
            foreach (var block in prompt.Blocks)
            {
                _blocks.Children.Add(new TextBlock
                {
                    Text = BlockText.Render(block),
                    TextWrapping = TextWrapping.WrapWholeWords,
                    FontSize = 14,
                });
            }
        }

        var allowed = prompt.ActionAllowed != false;
        if (prompt.Choices is not null)
        {
            foreach (var choice in prompt.Choices)
                _choices.Children.Add(BuildChoice(choice, allowed));
        }
    }

    private UIElement BuildChoice(PromptChoice choice, bool allowed)
    {
        var kind = choice.EffectiveKind.ToLowerInvariant();
        var label = choice.Label ?? choice.Id ?? kind;

        if (kind is "text")
        {
            var box = new TextBox
            {
                Header = label,
                IsEnabled = allowed,
                AcceptsReturn = false,
            };
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                e.Handled = true;
                await SelectTextAsync(choice, box.Text);
            };
            var send = new Button { Content = "Send", IsEnabled = allowed, Margin = new Thickness(0, 8, 0, 0) };
            send.Click += async (_, _) => await SelectTextAsync(choice, box.Text);
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(box);
            stack.Children.Add(send);
            return stack;
        }

        if (kind is "voice" or "image")
        {
            return new TextBlock
            {
                Text = $"{label} ({kind} not supported on desktop v1 — use text/option)",
                Opacity = 0.6,
                TextWrapping = TextWrapping.WrapWholeWords,
            };
        }

        var btn = new Button
        {
            Content = label,
            IsEnabled = allowed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        btn.Click += async (_, _) => await SelectAsync(choice, payload: null);
        return btn;
    }

    private async Task SelectTextAsync(PromptChoice choice, string? text)
    {
        var payload = DocaJson.Deserialize<System.Text.Json.JsonElement>(
            DocaJson.Serialize(new { kind = "text", text = text ?? "" }));
        await SelectAsync(choice, payload);
    }

    private async Task SelectAsync(PromptChoice choice, System.Text.Json.JsonElement? payload)
    {
        if (_prompt.Id is null || choice.Id is null) return;
        _status.Text = "Sending…";
        try
        {
            var req = new SelectionRequest
            {
                SelectionId = _selection.Ensure(),
                ChoiceId = choice.Id,
                Payload = payload,
            };
            var status = await _client.SelectAsync(_prompt.Id, req);
            if ((int)status == 202)
            {
                _status.Text = "Waiting for answer…";
                var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    var latest = await _client.GetPromptAsync(_prompt.Id);
                    if (latest is null) continue;
                    Bind(latest, _client);
                    var state = latest.State ?? "";
                    // "open" is the state a prompt is created in (api-v1/prompts.js sets
            // it at creation and as the per-device default), so treating it as
            // terminal ended this wait on its first poll — one second after the
            // 202, the user was left looking at a re-rendered prompt with a
            // blank status line and no sign that anything was still in flight.
            if (state is "outcome_ready" or "confirmed" or "closed" or "dismissed")
                    {
                        if (state is "outcome_ready")
                            _status.Text = "Outcome ready — Confirm or Back.";
                        return;
                    }
                }
                _status.Text = "No answer came back (resolver timeout).";
                return;
            }

            if (string.Equals(choice.EffectiveKind, "dismiss", StringComparison.OrdinalIgnoreCase))
            {
                _status.Text = "Dismissed";
                Close();
                return;
            }

            // Show confirm/back for option selections (outcome_ready)
            ShowConfirmBar();
            _status.Text = "Confirm or Back";
        }
        catch (StaleSelectionException)
        {
            _status.Text = "Selection stale — refreshed.";
            var latest = await _client.GetPromptAsync(_prompt.Id);
            if (latest is not null) Bind(latest, _client);
        }
        catch (PromptClosedException)
        {
            _status.Text = "Already answered elsewhere.";
            Close();
        }
        catch (SelectionConflictException)
        {
            _selection.NewId();
            _status.Text = "Conflict — try again.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void ShowConfirmBar()
    {
        // Remove previous confirm bar if any
        for (var i = _choices.Children.Count - 1; i >= 0; i--)
        {
            if (_choices.Children[i] is StackPanel sp && sp.Tag as string == "confirm-bar")
                _choices.Children.RemoveAt(i);
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Tag = "confirm-bar" };
        var confirm = new Button { Content = "Confirm", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var back = new Button { Content = "Back" };
        confirm.Click += async (_, _) =>
        {
            try
            {
                await _client.ConfirmAsync(_prompt.Id!, new ConfirmRequest
                {
                    SelectionId = _selection.Current,
                    Decision = "confirm",
                });
                Close();
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        back.Click += async (_, _) => await BackAsync();
        bar.Children.Add(confirm);
        bar.Children.Add(back);
        _choices.Children.Add(bar);
    }

    public async Task BackAsync()
    {
        if (_prompt.Id is null) return;
        try
        {
            await _client.ConfirmAsync(_prompt.Id, new ConfirmRequest
            {
                SelectionId = _selection.Current,
                Decision = "back",
            });
            _selection.AfterBack();
            var latest = await _client.GetPromptAsync(_prompt.Id);
            if (latest is not null) Bind(latest, _client);
            _status.Text = "Back — choose again";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }
}
