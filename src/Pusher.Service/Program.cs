using Pusher.Core.Abstractions;
using Pusher.Git;
using Pusher.Service;
using Pusher.Storage;
using Serilog;

var settingsStore = new AppSettingsStore();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(settingsStore.BaseDirectory, "logs", "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = "GitHubSchedulePusher");
    builder.Services.AddSerilog();

    builder.Services.AddSingleton(settingsStore);
    builder.Services.AddSingleton<IStateStore>(new SqliteStateStore(settingsStore.DatabasePath));
    builder.Services.AddSingleton<IGitEngine, GitEngine>();
    builder.Services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
    builder.Services.AddHostedService<PushWorker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
