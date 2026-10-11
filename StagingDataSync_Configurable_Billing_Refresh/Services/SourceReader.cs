using Dapper;
using MySqlConnector;
using System.Dynamic;
using System.Data;

namespace Services
{
    public class SourceReader
    {
        private readonly string _conn;
        private readonly int _commandTimeoutSeconds;

        // Per-table primary key overrides for cases where INFORMATION_SCHEMA or heuristics
        // do not return the desired identity column.
        private static readonly Dictionary<string, string> _primaryKeyOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            { "verificationlogicresults", "VerificationId" },
            { "verificationfieldresults", "VerificationLogicResultId" }
        };

        public SourceReader(string connectionString)
        {
            _conn = connectionString;
            // Command timeout (seconds). Use specific source var or general COMMAND_TIMEOUT_SECONDS.
            var env = Environment.GetEnvironmentVariable("SOURCE_COMMAND_TIMEOUT_SECONDS");
            if (!int.TryParse(env, out _commandTimeoutSeconds))
            {
                var env2 = Environment.GetEnvironmentVariable("COMMAND_TIMEOUT_SECONDS");
                if (!int.TryParse(env2, out _commandTimeoutSeconds)) _commandTimeoutSeconds = 0; // 0 = no timeout for Dapper
            }
        }

        public async Task<List<dynamic>> QueryRowsAsync(string schema, string table, string columnsCsv, (string column, DateTime? lastLoad)? deltaFilter, int fetchBatch)
        {
            using var c = new MySqlConnection(_conn);
            await c.OpenAsync();

            var cols = NormalizeColumns(columnsCsv);
            var colList = string.Join(", ", cols.Select(cn => $"`{cn}`"));

            var sql = $"SELECT {colList} FROM `{schema}`.`{table}`";
            var parameters = new DynamicParameters();
            if (deltaFilter.HasValue && deltaFilter.Value.lastLoad.HasValue)
            {
                // normalize to UTC to match stored watermark values
                var last = DateTime.SpecifyKind(deltaFilter.Value.lastLoad.Value, DateTimeKind.Utc);
                sql += $" WHERE `{deltaFilter.Value.column}` > @LastLoad";
                parameters.Add("LastLoad", last, DbType.DateTime);
            }

            var rows = await c.QueryAsync(sql, parameters, commandTimeout: _commandTimeoutSeconds);
            return rows.Cast<dynamic>().ToList();
        }

        public async Task<string?> GetPrimaryKeyColumnAsync(string schema, string table)
        {
            // Honor explicit per-table overrides first
            if (!string.IsNullOrEmpty(table) && _primaryKeyOverrides.TryGetValue(table, out var overrideCol))
            {
                return overrideCol;
            }
            using var c = new MySqlConnection(_conn);
            var sql1 = @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table AND EXTRA LIKE '%auto_increment%' LIMIT 1";
            var found = await c.ExecuteScalarAsync<string?>(sql1, new { Schema = schema, Table = table });
            if (!string.IsNullOrEmpty(found)) return found;

            var sql2 = @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table AND CONSTRAINT_NAME='PRIMARY' LIMIT 1";
            var pk = await c.ExecuteScalarAsync<string?>(sql2, new { Schema = schema, Table = table });
            // Return whatever INFORMATION_SCHEMA reports as the primary key (or null).
            // We only apply explicit per-table overrides above; do not heuristically pick
            // a different column for other tables.
            return pk;
        }

        private static IEnumerable<string> NormalizeColumns(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Enumerable.Empty<string>();
            return csv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
        }
    }
}
