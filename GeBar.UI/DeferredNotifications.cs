namespace GeBar.UI;

internal sealed class DeferredNotifications(Func<bool> allowed, Func<string, string, bool> show)
{
    private readonly Queue<(string Title, string Text)> _pending = new();
    public bool Pending => _pending.Count > 0;

    public void Add(string title, string text, bool defer = true)
    {
        if (!defer)
        {
            if (allowed()) show(title, text);
            return;
        }
        if (_pending.Contains((title, text))) return;
        if (_pending.Count == 16) _pending.Dequeue();
        _pending.Enqueue((title, text));
    }

    public void Flush()
    {
        if (!Pending || !allowed()) return;
        (string title, string text) = _pending.Peek();
        if (show(title, text)) _pending.Dequeue();
    }
}
