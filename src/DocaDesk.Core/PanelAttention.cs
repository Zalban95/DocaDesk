using System.Text.Json;

namespace DocaDesk.Core;

/// <summary>What in the panel is waiting for the person: a password, a question, an agent asking to act.</summary>
public enum AttentionKind { Password, Question, Approval }

/// <summary>What to do after a change: show a notification (title and text, never anything from the page), and the badge.</summary>
public sealed record AttentionAction(string? ToastTitle, string? ToastText, bool Badge, bool ClearToast);

/// <summary>
/// The panel's dialogs that need the person, told to DocaDesk so a hidden or background window can say so (a Windows
/// notification that opens the window, and a mark on the tray icon until it is answered).
///
/// The page side is <see cref="Script"/>: a watcher DocaDesk adds to the panel's top frame. It reads only <b>which</b>
/// of the panel's dialogs are open — the password prompt, the question prompt and confirm, the approval popup
/// (DOCA public/js/lib/dialogs.js, agent-ui/approval.js) — and posts <c>{type: "doca.attention", kinds: [...]}</c>.
/// No dialog text, no field value and no command ever leaves the page: the notification's words are fixed here, so a
/// password typed into the prompt, or a secret in an approval's command line, cannot reach a toast or the log.
/// A hub that posts the same message itself (see AGENTS.md) is read the same way.
/// </summary>
public sealed class PanelAttention
{
    public const string MessageType = "doca.attention";

    private HashSet<AttentionKind> _open = [];
    private readonly HashSet<AttentionKind> _told = [];

    /// <summary>What is open now.</summary>
    public IReadOnlyCollection<AttentionKind> Open => _open;

    /// <summary>
    /// The kinds a web message names, or null when it is not one of ours (wrong shape, unknown kind, too long): a
    /// message DocaDesk cannot read changes nothing.
    /// </summary>
    public static HashSet<AttentionKind>? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 512) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != MessageType
                || !r.TryGetProperty("kinds", out var ks) || ks.ValueKind != JsonValueKind.Array || ks.GetArrayLength() > 3)
                return null;
            var set = new HashSet<AttentionKind>();
            foreach (var k in ks.EnumerateArray())
            {
                switch (k.ValueKind == JsonValueKind.String ? k.GetString() : null)
                {
                    case "password": set.Add(AttentionKind.Password); break;
                    case "question": set.Add(AttentionKind.Question); break;
                    case "approval": set.Add(AttentionKind.Approval); break;
                    default: return null;
                }
            }
            return set;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The page's dialogs are now <paramref name="kinds"/>. A kind that has just opened is told once while the window
    /// is not in front (<paramref name="inFront"/>: visible and the foreground window — the person is looking at it);
    /// the badge stays while anything is open, and the notification is taken back once nothing is.
    /// </summary>
    public AttentionAction Update(IEnumerable<AttentionKind> kinds, bool inFront)
    {
        var now = kinds.ToHashSet();
        _told.IntersectWith(now);   // closed and opened again is a new question
        var fresh = now.Except(_told).ToList();
        _open = now;
        if (now.Count == 0)
            return new AttentionAction(null, null, Badge: false, ClearToast: true);
        if (inFront)
        {
            _told.UnionWith(now);   // seen where it was asked: no notification for it later
            return new AttentionAction(null, null, Badge: false, ClearToast: true);
        }
        if (fresh.Count == 0)
            return new AttentionAction(null, null, Badge: true, ClearToast: false);
        _told.UnionWith(fresh);
        var (title, text) = Words(fresh.Contains(AttentionKind.Approval) ? AttentionKind.Approval
            : fresh.Contains(AttentionKind.Password) ? AttentionKind.Password : AttentionKind.Question);
        return new AttentionAction(title, text, Badge: true, ClearToast: false);
    }

    /// <summary>The window came to the front: whatever is open has been seen there.</summary>
    public AttentionAction Shown()
    {
        _told.UnionWith(_open);
        return new AttentionAction(null, null, Badge: false, ClearToast: true);
    }

    /// <summary>The window went to the background or the tray with something still open: the mark comes back.</summary>
    public bool BadgeWhenHidden => _open.Count > 0;

    public static (string Title, string Text) Words(AttentionKind kind) => kind switch
    {
        AttentionKind.Approval => ("The agent is asking to do something", "Open DocaDesk to allow or deny it."),
        AttentionKind.Password => ("DOCA needs your password", "Open DocaDesk to confirm it there. It is never shown here."),
        _ => ("DOCA is asking you something", "Open DocaDesk to answer."),
    };

    /// <summary>
    /// The watcher DocaDesk adds to the panel (top frame only). It posts the set of open dialogs whenever it changes,
    /// and once at load so a reload clears what the last page left open. Reads ids and classes only.
    /// </summary>
    public const string Script = """
        (() => {
          if (window !== window.top || !window.chrome || !window.chrome.webview) return;
          let last = null;
          const kinds = () => {
            const k = [];
            const p = document.getElementById('app-prompt-modal');
            if (p && p.classList.contains('open')) {
              const i = document.getElementById('app-prompt-input');
              k.push(i && i.type === 'password' ? 'password' : 'question');
            }
            const c = document.getElementById('app-confirm-modal');
            if (c && c.classList.contains('open') && !k.includes('question')) k.push('question');
            if (document.getElementById('approval-overlay')) k.push('approval');
            return k;
          };
          const tell = () => {
            const k = kinds(), s = k.join(',');
            if (s === last) return;
            last = s;
            try { window.chrome.webview.postMessage({ type: 'doca.attention', kinds: k }); } catch (_) { }
          };
          const start = () => {
            tell();
            new MutationObserver(tell).observe(document.documentElement,
              { subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'type'] });
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
          else start();
        })();
        """;
}
