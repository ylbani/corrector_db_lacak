using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DbSchemaSyncAuditor.Services;

public class SqlScriptRunner
{
    public async Task<(bool Success, string Logs)> ExecuteScriptAsync(string connectionString, string fullScript, CancellationToken ct = default)
    {
        var logs = new System.Text.StringBuilder();

        var batches = Regex.Split(fullScript, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                           .Where(b => !string.IsNullOrWhiteSpace(b));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        connection.InfoMessage += (_, args) =>
        {
            logs.AppendLine($"[INFO] {args.Message}");
        };

        foreach (var batch in batches)
        {
            await using var command = new SqlCommand(batch, connection);
            // 0 = Sin límite de tiempo (evita 'Execution Timeout Expired' en tablas masivas)
            command.CommandTimeout = 0;
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                logs.AppendLine($"[ERROR] {ex.Message}");
                return (false, logs.ToString());
            }
        }

        return (true, logs.ToString());
    }
}
