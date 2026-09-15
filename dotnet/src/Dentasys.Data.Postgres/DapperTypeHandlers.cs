using System.Data;
using Dapper;

namespace Dentasys.Data.Postgres;

/// <summary>
/// Dapper does not map DateOnly/TimeOnly as command parameters out of the box,
/// though Npgsql handles both natively once they reach it.
///
/// Worth using the precise types rather than falling back to DateTime: a schedule
/// row has a wall-clock date and a wall-clock time and neither is an instant. A
/// DateTime here would invite exactly the silent zone conversion the whole
/// migration exists to avoid.
///
/// Registered explicitly by the composition root rather than from a module
/// initializer. Dapper's handler table is process-global mutable state, and a
/// library that mutates it just by being referenced is the kind of action at a
/// distance that is miserable to debug from a stack trace.
/// </summary>
public static class DapperTypeHandlers
{
    private static int _registered;

    /// <summary>Idempotent. Call once from the composition root.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s),
            _ => throw new DataException($"cannot convert {value.GetType()} to DateOnly"),
        };
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.Time;
            parameter.Value = value;
        }

        public override TimeOnly Parse(object value) => value switch
        {
            TimeOnly t => t,
            TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            DateTime dt => TimeOnly.FromDateTime(dt),
            string s => TimeOnly.Parse(s),
            _ => throw new DataException($"cannot convert {value.GetType()} to TimeOnly"),
        };
    }
}
