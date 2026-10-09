using Dentasys.Data.Postgres;
using Dentasys.Worker;

DapperTypeHandlers.Register();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(new PostgresOptions
{
    ConnectionString = builder.Configuration.GetConnectionString("Target")
        ?? Environment.GetEnvironmentVariable("DENTASYS_TARGET_CONNECTION")
        ?? "Host=localhost;Port=15432;Database=dentasys;Username=dentasys;Password=dentasys",
});

// --once: drain until empty and exit -- for a scheduler (Container Apps job,
// Kubernetes CronJob, ECS scheduled task). Without it, poll forever.
if (args.Contains("--once"))
{
    builder.Services.AddSingleton<OutboxDrainer>();
    using var host = builder.Build();
    await host.Services.GetRequiredService<OutboxDrainer>().DrainUntilEmptyAsync();
    return;
}

builder.Services.AddHostedService<OutboxDrainer>();

builder.Build().Run();
