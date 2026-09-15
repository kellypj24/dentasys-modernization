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

builder.Services.AddHostedService<OutboxDrainer>();

builder.Build().Run();
