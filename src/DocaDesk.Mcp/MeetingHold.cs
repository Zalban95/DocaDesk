namespace DocaDesk.Mcp;

/// <summary>
/// The person pressed Stop on the "&lt;name&gt; is controlling this computer" banner: until the hub says that control
/// ended (or a few seconds pass), the input tools refuse — so a pointer move or a keystroke already on its way from the
/// meeting does not land after Stop. The hub ends the grant itself; this only closes the gap.
/// </summary>
public static class MeetingHold
{
    private static long _untilTicks;

    public static void Hold(TimeSpan forHowLong) => Interlocked.Exchange(ref _untilTicks, DateTime.UtcNow.Add(forHowLong).Ticks);

    public static void Release() => Interlocked.Exchange(ref _untilTicks, 0);

    public static bool Held => DateTime.UtcNow.Ticks < Interlocked.Read(ref _untilTicks);

    public const string Refusal = "The person at this computer stopped control from a meeting: input is refused for a moment.";
}
