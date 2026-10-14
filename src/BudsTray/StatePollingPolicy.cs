namespace RedmiBudsTray;

public static class StatePollingPolicy
{
    public static bool ShouldQuery(long now, long lastQuery, long lastReply, bool visible)
    {
        // Foreground stays fresh; background notifications postpone fallback queries,
        // but never postpone a full battery/mode snapshot beyond one minute.
        if (visible) return now - lastQuery >= TimeSpan.FromSeconds(2).Ticks;
        return now - lastQuery >= TimeSpan.FromSeconds(60).Ticks ||
            now - Math.Max(lastQuery, lastReply) >= TimeSpan.FromSeconds(30).Ticks;
    }
}
