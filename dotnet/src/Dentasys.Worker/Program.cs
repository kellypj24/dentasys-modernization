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
//
// --recall [yyyy-MM-dd]: the nightly recall run, then drain what it enqueued.
// Replaces the 2 AM SQL Agent job. The date defaults to today in UTC; recall is
// month-grained, so the only night that could care which zone "today" is in is
// the last of the month.
var recall = Array.IndexOf(args, "--recall");
if (recall >= 0 || args.Contains("--once"))
{
    builder.Services.AddSingleton<OutboxDrainer>();
    builder.Services.AddSingleton(sp => new PostgresRecallStore(sp.GetRequiredService<PostgresOptions>()));
    using var host = builder.Build();

    if (recall >= 0)
    {
        var runDate = recall + 1 < args.Length && DateOnly.TryParse(args[recall + 1], out var d)
            ? d : DateOnly.FromDateTime(DateTime.UtcNow);
        var sent = await host.Services.GetRequiredService<PostgresRecallStore>()
            .RunAsync(runDate, actorId: "recall-job");
        host.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("recall run {RunDate}: {Sent} recall(s) due", runDate, sent);
    }

    await host.Services.GetRequiredService<OutboxDrainer>().DrainUntilEmptyAsync();
    return;
}

builder.Services.AddHostedService<OutboxDrainer>();

builder.Build().Run();
