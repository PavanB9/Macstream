using System.Diagnostics;
using System.IO;
using MacstreamViewer.Diagnostics;

namespace MacstreamViewer.Tray;

/// <summary>
/// Finds and stops the other Macstream processes on this PC for "Quit everything": a second
/// viewer (Macstream.exe), NetworkTest, and the
/// .NET build helpers (MSBuild nodes, compiler server) that the
/// project's own SDK in .local\dotnet leaves running after a build. Only processes whose
/// program file is inside the Macstream folder are touched.
/// </summary>
public static class MacstreamProcesses
{
    private static readonly string[] Names = { "Macstream", "NetworkTest", "dotnet" };

    /// <summary>The project folder (the one with Macstream.cmd), or null if not found.</summary>
    public static string? ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir != null && i < 8; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Macstream.cmd")) &&
                File.Exists(Path.Combine(dir.FullName, "tools", "env.ps1")))
                return dir.FullName;
        }
        return null;
    }

    public static void StopOthers()
    {
        var root = ProjectRoot();
        if (root == null)
        {
            DiagLog.Write("Quit everything: project folder not found; only closing this window");
            return;
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var self = Environment.ProcessId;
        var stopped = 0;
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == self)
                            continue;
                        var path = process.MainModule?.FileName;
                        if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        process.Kill();
                        stopped++;
                        DiagLog.Write($"Quit everything: stopped {name} (pid {process.Id}, {path})");
                    }
                    catch (Exception ex)
                    {
                        // Exited meanwhile, or belongs to another user: leave it alone.
                        DiagLog.Write($"Quit everything: skipped {name}: {ex.Message}");
                    }
                }
            }
        }
        DiagLog.Write($"Quit everything: stopped {stopped} other process(es)");
    }
}
