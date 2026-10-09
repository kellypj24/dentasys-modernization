using Dapper;
using Microsoft.Data.SqlClient;

namespace Dentasys.Parity;

/// <summary>
/// Runs usp_NightlyRecallAndClaims and returns the RECALL_IDs it sent. The proc
/// stamps LAST_SENT_DT as it goes, so it runs inside a transaction that is always
/// rolled back -- otherwise every probe would change the next one's answer.
/// </summary>
public sealed class LegacyRecallReader
{
    private readonly string _serverConnectionString;

    public LegacyRecallReader(string serverConnectionString) =>
        _serverConnectionString = serverConnectionString;

    public async Task<IReadOnlySet<long>> RunAsync(string practiceId, DateOnly runDate, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = $"DENTASYS_{practiceId}"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            var ids = await conn.QueryAsync<int>(new CommandDefinition(
                "usp_NightlyRecallAndClaims",
                new { RUN_DT = runDate.ToString("yyyyMMdd") },
                transaction: tx,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));
            return ids.Select(i => (long)i).ToHashSet();
        }
        finally
        {
            tx.Rollback();
        }
    }
}
