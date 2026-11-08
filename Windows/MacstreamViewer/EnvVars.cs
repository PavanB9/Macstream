namespace MacstreamViewer;

/// <summary>
/// Reads Macstream's environment-variable settings. They are named MACSTREAM_*; the older
/// WIRED_MONITOR_* name of the same setting is still read when the new one isn't set.
/// </summary>
internal static class EnvVars
{
    private const string Prefix = "MACSTREAM_";
    private const string OldPrefix = "WIRED_MONITOR_";

    public static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value == null && name.StartsWith(Prefix, StringComparison.Ordinal))
            value = Environment.GetEnvironmentVariable(OldPrefix + name[Prefix.Length..]);
        return value;
    }
}
