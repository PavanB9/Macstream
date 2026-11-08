using System.IO;

namespace MacstreamViewer.Diagnostics;

public static class DiagLog
{
    private static readonly object Gate = new();
    private const long MaxBytesAtStart = 5 * 1024 * 1024;
    private static readonly string PathValue = System.IO.Path.Combine(AppContext.BaseDirectory, "diag.log");
    private static readonly StreamWriter Writer = Open();

    /// <summary>Appends to diag.log; once it is over 5 MB at start-up it becomes
    /// diag.previous.log (replacing the older one), so the log never grows without limit.</summary>
    private static StreamWriter Open()
    {
        try
        {
            var info = new FileInfo(PathValue);
            if (info.Exists && info.Length > MaxBytesAtStart)
                File.Move(PathValue, System.IO.Path.Combine(AppContext.BaseDirectory, "diag.previous.log"), overwrite: true);
        }
        catch (IOException)
        {
            // Another viewer has it open: keep appending.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return new StreamWriter(new FileStream(PathValue, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public static string Path => PathValue;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            Writer.WriteLine(line);
        }
    }

    public static void Write(Exception ex, string message)
    {
        Write($"{message}: {ex}");
    }
}
