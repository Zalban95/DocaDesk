using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DocaDesk.Mcp;

/// <summary>
/// How Windows uses a sealed secret (docs/api/sealed-secrets.md, "DocaDesk"): typed with <c>SendInput</c>'s
/// <c>KEYEVENTF_UNICODE</c> into the foreground window — the path <c>input_type</c> takes; put on the clipboard with
/// the formats that keep it out of Win+V history, the cloud clipboard and clipboard monitors, and cleared after its
/// time if it is still there; or filled into a credential field of the panel's own WebView2 (<see cref="ISecretField"/>,
/// built in the app, which owns the window).
/// </summary>
public sealed class WindowsSecretSink(Func<ISecretField?> field) : ISecretSink
{
    public Task TypeAsync(string value, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new SecretRefusedException("Typing a secret needs Windows.");
        var inputs = new List<Native.Input>(value.Length * 2);
        foreach (var c in value) { inputs.Add(Native.Char(c, false)); inputs.Add(Native.Char(c, true)); }
        try { Native.Send(inputs); }
        catch (InvalidOperationException ex) { throw new SecretRefusedException(ex.Message); }   // Native.Send's sentence names no character
        finally { inputs.Clear(); }
        return Task.CompletedTask;
    }

    public Task<ISecretClip> ClipAsync(string value, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new SecretRefusedException("No clipboard on this machine.");
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(value));
        SecretClipboard.Set(value);
        return Task.FromResult<ISecretClip>(new Clip(hash));
    }

    public Task FieldAsync(string origin, int? tab, int reference, string value, CancellationToken ct) =>
        (field() ?? throw new SecretRefusedException("DocaDesk's panel window is not open, so there is no page to fill: open DocaDesk and try again."))
            .FillAsync(origin, tab, reference, value, ct);

    private sealed class Clip(byte[] hash) : ISecretClip
    {
        public Task StopAsync()
        {
            SecretClipboard.ClearIfHolds(hash);   // something the person copied since is theirs
            return Task.CompletedTask;
        }
    }
}

/// <summary>The Win32 clipboard, held for one write or one check by a message-only window made and destroyed here.</summary>
internal static class SecretClipboard
{
    private const uint CfUnicodeText = 13, GmemMoveable = 0x2;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr mem);

    /// <summary>The text, plus the three formats Windows reads as "keep this out of history, the cloud and monitors".</summary>
    public static void Set(string value)
    {
        WithClipboard(() =>
        {
            if (!EmptyClipboard()) throw new SecretRefusedException("Windows would not let DocaDesk take the clipboard.");
            var bytes = new byte[(value.Length + 1) * 2];
            Encoding.Unicode.GetBytes(value, 0, value.Length, bytes, 0);
            try
            {
                Put(CfUnicodeText, bytes);
                var zero = new byte[4];   // a DWORD 0: "no"
                Put(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), zero);
                Put(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), zero);
                Put(RegisterClipboardFormatW("CanUploadToCloudClipboard"), zero);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
    }

    /// <summary>Empty the clipboard if its text is still the secret (compared by hash).</summary>
    public static void ClearIfHolds(byte[] hash)
    {
        WithClipboard(() =>
        {
            var mem = GetClipboardData(CfUnicodeText);
            if (mem == IntPtr.Zero) return;
            var p = GlobalLock(mem);
            if (p == IntPtr.Zero) return;
            byte[] now;
            try
            {
                var text = Marshal.PtrToStringUni(p, (int)Math.Min((ulong)GlobalSize(mem) / 2, int.MaxValue)) ?? "";
                var end = text.IndexOf('\0');
                now = Encoding.Unicode.GetBytes(end >= 0 ? text[..end] : text);
            }
            finally { GlobalUnlock(mem); }
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(now), hash)) EmptyClipboard();
            CryptographicOperations.ZeroMemory(now);
        });
    }

    private static void Put(uint format, byte[] bytes)
    {
        if (format == 0) return;
        var mem = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (mem == IntPtr.Zero) throw new SecretRefusedException("Windows had no memory for the clipboard.");
        var p = GlobalLock(mem);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) == IntPtr.Zero)
        {
            // Not handed over: ours to wipe and free.
            p = GlobalLock(mem);
            Marshal.Copy(new byte[bytes.Length], 0, p, bytes.Length);
            GlobalUnlock(mem);
            GlobalFree(mem);
            throw new SecretRefusedException("Windows refused the clipboard write.");
        }
    }

    /// <summary>Open the clipboard (it may be busy for a moment), run, close — all on this thread, as Win32 wants.</summary>
    private static void WithClipboard(Action work)
    {
        var owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var open = false;
            for (var i = 0; i < 20 && !(open = OpenClipboard(owner)); i++) Thread.Sleep(25);
            if (!open) throw new SecretRefusedException("The clipboard is busy with another program: try again.");
            try { work(); }
            finally { CloseClipboard(); }
        }
        finally { if (owner != IntPtr.Zero) DestroyWindow(owner); }
    }
}
