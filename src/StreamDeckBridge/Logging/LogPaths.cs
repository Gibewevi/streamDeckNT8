namespace StreamDeckBridge.Logging;

/// <summary>
/// Resolves the shared log directory used by all three components of the cockpit.
/// Kept next to the other runtime state (safety-macro.json, session.json) so a support
/// request only ever has to zip one folder.
/// </summary>
public static class LogPaths
{
    /// <summary>Environment override, also honoured by the plugin and the NT8 add-on.</summary>
    public const string DirectoryEnvVar = "STREAMDECK_TRADER_LOG_DIR";

    public static string ResolveDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured);

        var fromEnv = Environment.GetEnvironmentVariable(DirectoryEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Environment.ExpandEnvironmentVariables(fromEnv);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader",
            "logs");
    }
}
