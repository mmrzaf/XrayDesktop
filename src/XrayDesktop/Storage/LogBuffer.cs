namespace XrayDesktop.Storage;

internal sealed class LogBuffer
{
    private readonly object sync = new();
    private readonly Queue<(long Id, string Text)> lines = new();
    private long sequence;
    private int chars;
    internal void Add(string source, string message)
    {
        var text = $"{DateTimeOffset.Now:HH:mm:ss} [{source}] {message[..Math.Min(message.Length, 4096)]}";
        lock (sync)
        {
            lines.Enqueue((++sequence, text));
            // Account for CRLF in the displayed/exported Windows text as well.
            chars += text.Length + 2;
            while (lines.Count > 5000 || chars > 2 * 1024 * 1024) chars -= lines.Dequeue().Text.Length + 2;
        }
    }
    internal (long Id, string[] Lines) ReadAfter(long id)
    {
        lock (sync) return (sequence, lines.Where(l => l.Id > id).Select(l => l.Text).ToArray());
    }
    internal void Clear() { lock (sync) { lines.Clear(); chars = 0; } }
}
