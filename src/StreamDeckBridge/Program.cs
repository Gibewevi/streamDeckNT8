using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamDeckBridge;
using StreamDeckBridge.Models;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
var config = new BridgeConfig();
builder.Services.AddSingleton(config);

// Logging
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Debug);
});

// Services
builder.Services.AddSingleton<StateManager>();
builder.Services.AddSingleton<MessageValidator>();
builder.Services.AddSingleton(sp =>
    new DuplicateGuard(config.DuplicateRequestWindowSeconds, sp.GetRequiredService<ILogger<DuplicateGuard>>()));
builder.Services.AddSingleton<MessageRouter>();
builder.Services.AddHostedService<BridgeServer>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== StreamDeck Trading Bridge V1.0 ===");
logger.LogInformation("Plugin port: {Port}", config.PluginPort);
logger.LogInformation("Add-On port: {Port}", config.AddonPort);
logger.LogInformation("Default account: {Account}", config.DefaultAccount);
logger.LogInformation("Default instrument: {Instrument}", config.DefaultInstrument);
logger.LogInformation("Safe mode: {SafeMode}", !config.AllowLiveAccounts ? "ON" : "OFF");

await host.RunAsync();
