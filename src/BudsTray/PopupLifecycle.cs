namespace RedmiBudsTray;

public enum PopupPhase { Hidden, Opening, Open, Closing, Menu, Exited }

// UI-thread-owned state: a new intent invalidates all earlier completions.
public sealed class PopupLifecycle
{
    public PopupPhase Phase { get; private set; }
    public int Revision { get; private set; }
    public int Begin(PopupPhase phase)
    {
        if (Phase == PopupPhase.Exited) return Revision;
        Phase = phase;
        return ++Revision;
    }
    public bool Complete(int revision, PopupPhase expected, PopupPhase next)
    {
        if (Revision != revision || Phase != expected || Phase == PopupPhase.Exited) return false;
        Phase = next;
        return true;
    }
}
