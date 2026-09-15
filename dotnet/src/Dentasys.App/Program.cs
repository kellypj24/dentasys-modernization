using Dentasys.App;
using Dentasys.Client;
using Dentasys.Data.SqlServer;
using Dentasys.Domain;

// dentasys <practiceId> <yyyy-MM-dd> [--legacy|--modern] [--grid N] [--include-deleted]
//
// The same screen, the same rules, two completely different ways of reaching the
// data. --legacy opens a connection from this process, the way every workstation
// did for 29 years. --modern asks the API and this process never learns where the
// database is.

var argv = args.ToList();

bool Flag(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0) return false;
    argv.RemoveAt(i);
    return true;
}

string? Opt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}

var legacy = Flag("--legacy");
var modern = Flag("--modern");
var includeDeleted = Flag("--include-deleted");
var gridArg = Opt("--grid");

if (argv.Count < 2)
{
    Console.Error.WriteLine("usage: dentasys <practiceId> <yyyy-MM-dd> [--legacy|--modern] [--grid N] [--include-deleted]");
    return 2;
}

var practiceId = argv[0];
if (!DateOnly.TryParse(argv[1], out var date))
{
    Console.Error.WriteLine($"not a date: {argv[1]}");
    return 2;
}

if (legacy && modern)
{
    Console.Error.WriteLine("choose one of --legacy or --modern");
    return 2;
}

var query = new ScheduleQuery
{
    PracticeId = practiceId,
    Date = date,
    IncludeDeleted = includeDeleted,
};

IScheduleSource source;
if (legacy)
{
    var cs = Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    // The grid comes from a command-line flag here because in the legacy world it
    // came from a workstation .INI file -- a per-desk setting the database never
    // saw. Passing it in is a faithful port of that, not a shortcut.
    source = new LegacyDirectScheduleSource(
        new SqlServerScheduleRepository(cs),
        new ScheduleService(),
        gridArg is null ? null : int.Parse(gridArg));
}
else
{
    var baseUrl = Environment.GetEnvironmentVariable("DENTASYS_API_URL") ?? "http://localhost:5179";
    source = new ApiScheduleSource(new HttpClient { BaseAddress = new Uri(baseUrl) });
}

try
{
    var result = await source.GetScheduleAsync(query);
    Console.Write(ScheduleRenderer.Render(result, practiceId, date));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
