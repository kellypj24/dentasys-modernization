using Dentasys.Notes;
using Dentasys.Notes.Api;
using Microsoft.Data.SqlClient;

// dotnet run -- issue-token --sub dr.lee --role clinician --practice 001204 [--prov DDS1]
// Lab only: prints a token signed with Notes:Auth:LabSigningKey.
if (args.FirstOrDefault() == "issue-token")
{
    string Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
    var key = Environment.GetEnvironmentVariable("Notes__Auth__LabSigningKey")
              ?? throw new InvalidOperationException("set Notes__Auth__LabSigningKey");
    Console.WriteLine(Auth.IssueLabToken(key, Opt("--sub"), Opt("--role"), Opt("--practice"),
                                         Opt("--prov") is { Length: > 0 } prov ? prov : null));
    return;
}

var builder = WebApplication.CreateBuilder(args);

var server = builder.Configuration["Notes:Server"]
    ?? Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
    ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

// The lab transcriber. A speech service implementing ITranscriber replaces it here.
ITranscriber transcriber = new FixtureTranscriber();
builder.Services.AddSingleton(transcriber);
builder.Services.AddSingleton(new NotesOptions
{
    NotesConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "DENTASYS_NOTES" }.ConnectionString,
    PracticeServerConnectionString = server,
    AudioFormats = transcriber.SupportedFormats,
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => DrafterSet.FromConfig(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<NotesService>();
builder.Services.AddSingleton(sp =>
{
    var d = sp.GetRequiredService<DrafterSet>();
    return new JobRunner(sp.GetRequiredService<NotesOptions>(), sp.GetRequiredService<ITranscriber>(),
                         d.Cloud, d.Local, sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<ChartWriter>();
if (builder.Configuration.GetValue("Notes:Workers", true)) builder.Services.AddHostedService<NotesWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddNotesAuth(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapNotesEndpoints();
app.Run();

/// <summary>Exposed so tests can host the service in-process.</summary>
public partial class Program;
