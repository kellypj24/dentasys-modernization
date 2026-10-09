using Dentasys.Notes;
using Dentasys.Notes.Api;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

var server = builder.Configuration["Notes:Server"]
    ?? Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
    ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

builder.Services.AddSingleton(new NotesOptions
{
    NotesConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "DENTASYS_NOTES" }.ConnectionString,
    PracticeServerConnectionString = server,
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => DrafterSet.FromConfig(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<NotesService>();
builder.Services.AddSingleton<ITranscriber, FixtureTranscriber>();
builder.Services.AddSingleton(sp =>
{
    var d = sp.GetRequiredService<DrafterSet>();
    return new JobRunner(sp.GetRequiredService<NotesOptions>(), sp.GetRequiredService<ITranscriber>(),
                         d.Cloud, d.Local, sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<ChartWriter>();
if (builder.Configuration.GetValue("Notes:Workers", true)) builder.Services.AddHostedService<NotesWorker>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapNotesEndpoints();
app.Run();

/// <summary>Exposed so tests can host the service in-process.</summary>
public partial class Program;
