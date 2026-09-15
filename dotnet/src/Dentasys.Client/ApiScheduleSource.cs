using System.Diagnostics;
using System.Net.Http.Json;
using Dentasys.Domain;

namespace Dentasys.Client;

/// <summary>
/// The modern path. An HttpClient and nothing else -- no connection string, no
/// credentials, no driver on the workstation at all.
///
/// Note what is NOT here: this class does not filter deleted rows, format names,
/// compute durations or sort. The API already ran those rules, once, server-side.
/// In the legacy stack every workstation ran its own copy of that logic and they
/// drifted between client versions, which is how the schedule and the production
/// report came to disagree for fifteen years.
/// </summary>
public sealed class ApiScheduleSource : IScheduleSource
{
    private readonly HttpClient _http;

    public ApiScheduleSource(HttpClient http) => _http = http;

    public string Description => $"API gate ({_http.BaseAddress})";

    public async Task<ScheduleResult> GetScheduleAsync(ScheduleQuery query, CancellationToken ct = default)
    {
        var url = $"/practices/{Uri.EscapeDataString(query.PracticeId)}/schedule"
                + $"?date={query.Date:yyyy-MM-dd}"
                + (query.OperatoryCode is { } o ? $"&operatory={Uri.EscapeDataString(o)}" : "")
                + (query.ProviderCode is { } p ? $"&provider={Uri.EscapeDataString(p)}" : "")
                + (query.IncludeDeleted ? "&includeDeleted=true" : "");

        var sw = Stopwatch.StartNew();
        var dto = await _http.GetFromJsonAsync<ScheduleResponse>(url, ct)
                  ?? throw new InvalidOperationException($"empty response from {url}");
        sw.Stop();

        return new ScheduleResult
        {
            Slots = dto.Slots.Select(Map).ToList(),
            Source = "API gate -> PostgreSQL",
            Elapsed = sw.Elapsed,
            Truncated = dto.Truncated,
            GridMinutes = dto.GridMinutes,
        };
    }

    private static ScheduleSlot Map(SlotDto d) => new()
    {
        AppointmentId = d.AppointmentId,
        LocalDate = DateOnly.Parse(d.Date),
        LocalTime = TimeOnly.Parse(d.Time),
        LengthUnits = d.LengthUnits,
        DurationMinutes = d.DurationMinutes,
        EndTime = d.EndTime is null ? null : TimeOnly.Parse(d.EndTime),
        EndsNextDay = d.EndsNextDay,
        PatientId = d.PatientId,
        PatientName = d.PatientName,
        HomePhone = d.HomePhone,
        Balance = d.Balance,
        ProviderCode = d.ProviderCode,
        ProviderName = d.ProviderName,
        OperatoryCode = d.OperatoryCode,
        OperatoryName = d.OperatoryName,
        ProcedureCode = d.ProcedureCode,
        ProcedureDescription = d.ProcedureDescription,
        StatusCode = d.StatusCode,
        Note = d.Note,
    };

    private sealed record ScheduleResponse(
        string PracticeId, string Date, int? GridMinutes,
        IReadOnlyList<SlotDto> Slots, bool Truncated);

    private sealed record SlotDto(
        long AppointmentId, string Date, string Time, int? LengthUnits, int? DurationMinutes,
        string? EndTime, bool EndsNextDay, long? PatientId, string? PatientName, string? HomePhone,
        decimal? Balance, string? ProviderCode, string? ProviderName, string? OperatoryCode,
        string? OperatoryName, string? ProcedureCode, string? ProcedureDescription,
        string? StatusCode, string? Note);
}
