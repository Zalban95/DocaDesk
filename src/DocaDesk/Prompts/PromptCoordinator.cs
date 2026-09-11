using System.Text.Json;
using DocaDesk.Core;
using DocaDesk.Core.Logging;
using DocaDesk.Core.Models;
using DocaDesk.Services;

namespace DocaDesk.Prompts;

/// <summary>Routes push events to notifications + prompt window. Alerts never become modals.</summary>
public sealed class PromptCoordinator
{
    private readonly AppSession _session;
    private readonly IDocaLogger _log;
    private readonly Dictionary<string, PromptWindow> _open = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PromptCoordinator(AppSession session, IDocaLogger? log = null)
    {
        _session = session;
        _log = log ?? new RedactingLogger();
        _session.EventReceived += OnEventAsync;
    }

    private async Task OnEventAsync(EventEnvelope ev, CancellationToken ct)
    {
        var type = ev.Type ?? "";
        if (string.Equals(type, "prompt.new", StringComparison.OrdinalIgnoreCase))
        {
            var prompt = ExtractPrompt(ev.Payload);
            if (prompt?.Id is null) return;
            await ShowPromptAsync(prompt, notify: true).ConfigureAwait(false);
            return;
        }

        if (string.Equals(type, "prompt.closed", StringComparison.OrdinalIgnoreCase))
        {
            var id = ExtractPromptId(ev.Payload);
            if (id is not null)
                ClosePromptWindow(id);
            return;
        }

        if (string.Equals(type, "prompt.updated", StringComparison.OrdinalIgnoreCase))
        {
            var id = ExtractPromptId(ev.Payload);
            if (id is null || _session.Client is null) return;
            try
            {
                var latest = await _session.Client.GetPromptAsync(id, ct).ConfigureAwait(false);
                if (latest is not null)
                    await ShowPromptAsync(latest, notify: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("prompt refresh failed: " + ex.Message);
            }
            return;
        }

        if (string.Equals(type, "alert", StringComparison.OrdinalIgnoreCase))
        {
            var title = ev.Payload?.TryGetProperty("title", out var t) == true ? t.GetString() : "Alert";
            var body = ExtractAlertBody(ev.Payload);
            NotificationService.ShowAlert(title ?? "Alert", body);
        }
    }

    public Task ShowPromptAsync(PromptDocument prompt, bool notify)
    {
        if (prompt.Id is null) return Task.CompletedTask;

        if (notify)
        {
            var preview = BlockText.Summarize(prompt);
            NotificationService.ShowPrompt(prompt.Id, prompt.Title ?? "Prompt", preview);
        }

        var tcs = new TaskCompletionSource();
        var dq = App.UiDispatcher;
        if (dq is null)
        {
            tcs.TrySetResult();
            return tcs.Task;
        }

        dq.TryEnqueue(() =>
        {
            try
            {
                OpenOrUpdateWindow(prompt);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void OpenOrUpdateWindow(PromptDocument prompt)
    {
        lock (_gate)
        {
            if (_session.Client is null) return;

            if (_open.TryGetValue(prompt.Id!, out var existing))
            {
                existing.Bind(prompt, _session.Client);
                existing.Activate();
                return;
            }

            var win = new PromptWindow(prompt, _session.Client);
            win.Closed += (_, _) =>
            {
                lock (_gate) _open.Remove(prompt.Id!);
            };
            _open[prompt.Id!] = win;
            win.Activate();
        }
    }

    public void ClosePromptWindow(string id)
    {
        PromptWindow? win;
        lock (_gate)
        {
            _open.TryGetValue(id, out win);
            _open.Remove(id);
        }
        win?.DispatcherQueue.TryEnqueue(() => win.Close());
    }

    private static PromptDocument? ExtractPrompt(JsonElement? payload)
    {
        if (payload is null) return null;
        var p = payload.Value;
        if (p.ValueKind != JsonValueKind.Object) return null;
        if (p.TryGetProperty("prompt", out var nested))
            return DocaJson.Deserialize<PromptDocument>(nested.GetRawText());
        return DocaJson.Deserialize<PromptDocument>(p.GetRawText());
    }

    private static string? ExtractPromptId(JsonElement? payload)
    {
        if (payload is null) return null;
        var p = payload.Value;
        if (p.TryGetProperty("promptId", out var id))
            return id.GetString();
        if (p.TryGetProperty("id", out var id2))
            return id2.GetString();
        if (p.TryGetProperty("prompt", out var nested) && nested.TryGetProperty("id", out var id3))
            return id3.GetString();
        return null;
    }

    private static string ExtractAlertBody(JsonElement? payload)
    {
        if (payload is null) return "";
        if (payload.Value.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var b in body.EnumerateArray())
            {
                if (b.TryGetProperty("display", out var d)) parts.Add(d.GetString() ?? "");
                else if (b.TryGetProperty("text", out var t)) parts.Add(t.GetString() ?? "");
            }
            return string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return payload.Value.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }
}

public static class BlockText
{
    public static string Summarize(PromptDocument prompt)
    {
        if (prompt.Blocks is null || prompt.Blocks.Count == 0)
            return prompt.Title ?? "";
        return string.Join(" · ", prompt.Blocks.Select(Render).Where(s => !string.IsNullOrWhiteSpace(s)).Take(3));
    }

    public static string Render(PromptBlock block)
    {
        var type = block.Type ?? "";
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            return block.Text ?? block.Display ?? "";
        if (!string.IsNullOrWhiteSpace(block.Display))
            return block.Display!;
        if (!string.IsNullOrWhiteSpace(block.Text))
            return block.Text!;
        return string.IsNullOrEmpty(type) ? "" : $"[{type}]";
    }
}
