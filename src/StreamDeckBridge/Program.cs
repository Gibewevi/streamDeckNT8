using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using StreamDeckBridge;
using StreamDeckBridge.Logging;
using StreamDeckBridge.Models;

var builder = Host.CreateApplicationBuilder(args);

// Configuration — defaults below, overridable per property with SDBRIDGE_ environment
// variables (e.g. SDBRIDGE_PluginPort=9218, SDBRIDGE_SafetyStatePath=C:\tmp\safety.json).
var config = new BridgeConfig();
new ConfigurationBuilder().AddEnvironmentVariables("SDBRIDGE_").Build().Bind(config);
builder.Services.AddSingleton(config);

// Logging — console for interactive runs, daily file for everything else. The plugin
// auto-launches the bridge detached with stdio ignored, so in normal use the file is the
// only record of what happened.
var logDirectory = LogPaths.ResolveDirectory(config.LogDirectory);
var fileLevel = Enum.TryParse<LogLevel>(config.FileLogLevel, ignoreCase: true, out var parsed)
    ? parsed
    : LogLevel.Debug;
var fileProvider = new FileLoggerProvider(logDirectory, fileLevel, config.LogRetentionDays, config.MaxLogFileSizeMb);

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddProvider(fileProvider);
    // Trace globally so the file can capture the chattiest events (periodic state pushes);
    // the console keeps its previous, readable level.
    logging.SetMinimumLevel(LogLevel.Trace);
    logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Debug);
});

// Services
builder.Services.AddSingleton<SecurityJournal>();
builder.Services.AddSingleton<SafetyMacro>();
builder.Services.AddSingleton<StateManager>();
builder.Services.AddSingleton<MessageValidator>();
builder.Services.AddSingleton(sp =>
    new DuplicateGuard(config.DuplicateRequestWindowSeconds, sp.GetRequiredService<ILogger<DuplicateGuard>>()));
builder.Services.AddSingleton<MessageRouter>();
builder.Services.AddHostedService<BridgeServer>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== StreamDeck Trading Bridge V1.0 ===");
logger.LogInformation("Process: PID {Pid}, .NET {Runtime}, machine {Machine}",
    Environment.ProcessId, Environment.Version, Environment.MachineName);
logger.LogInformation("Log file: {Path} (level {Level}, retention {Days} days)",
    fileProvider.CurrentPath, fileLevel, config.LogRetentionDays);
logger.LogInformation("Plugin port: {Port}", config.PluginPort);
logger.LogInformation("Add-On port: {Port}", config.AddonPort);
logger.LogInformation("Default account: {Account}", config.DefaultAccount);
logger.LogInformation("Default instrument: {Instrument}", config.DefaultInstrument);
logger.LogInformation("Safe mode: {SafeMode}", !config.AllowLiveAccounts ? "ON" : "OFF");

// Nothing else catches these: without the handlers a crashing background task or a failing
// finalizer leaves the log ending mid-sentence with no reason recorded.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        logger.LogCritical(ex, "Unhandled exception — process terminating: {Terminating}", e.IsTerminating);
    else
        logger.LogCritical("Unhandled non-exception throw — process terminating: {Terminating}", e.IsTerminating);
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    logger.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.LogInformation("Bridge process exiting");
    fileProvider.Dispose();
};

// Touch the safety macro before serving traffic so a restored lock is restored (and logged)
// before the first command can arrive.
host.Services.GetRequiredService<SafetyMacro>();

try
{
    await host.RunAsync();
    logger.LogInformation("Bridge stopped normally");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Bridge terminated by an unhandled exception");
    throw;
}
finally
{
    fileProvider.Dispose();
}
