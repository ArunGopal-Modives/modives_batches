using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace Services
{
    public class TargetWriter : IDisposable
    {
        private readonly string _conn;
        private readonly string _targetSchema;
        private readonly long _maxPacketSizeBytes;
        private readonly int _commandTimeoutSeconds;
        // Metrics for adaptive splitting/retries per table
        private readonly Dictionary<string, int> _splitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _retryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public TargetWriter(string connectionString, string targetSchema)
        {
            _conn = connectionString;
            _targetSchema = targetSchema;
            // Allow overriding packet threshold via env var; default to 32MB
            var env = Environment.GetEnvironmentVariable("MAX_PACKET_SIZE_BYTES");
            if (!long.TryParse(env, out _maxPacketSizeBytes)) _maxPacketSizeBytes = 32L * 1024 * 1024;
            // Allow configuring command timeout (in seconds) via env var. Default to 0 (no timeout) to support long-running operations.
            var envTimeout = Environment.GetEnvironmentVariable("COMMAND_TIMEOUT_SECONDS");
            if (!int.TryParse(envTimeout, out _commandTimeoutSeconds)) _commandTimeoutSeconds = 0;
        }

        /// <summary>
        /// Call a stored procedure on the target database. Procedure is qualified with the target schema.
        /// </summary>
        public async Task CallStoredProcedureAsync(string procName)
        {
            if (string.IsNullOrWhiteSpace(procName)) return;
            using var c = new MySqlConnection(_conn);
            await c.OpenAsync();
            var sql = $"CALL `{_targetSchema}`.`{procName}`()";
            var sw = Stopwatch.StartNew();
            try
            {
                // Use configured command timeout to avoid premature cancellation of long-running stored procedures.
                await c.ExecuteAsync(sql, commandTimeout: _commandTimeoutSeconds);
                sw.Stop();
                Console.WriteLine($"CallStoredProcedure: proc={procName} timeout={_commandTimeoutSeconds}s elapsed={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"CallStoredProcedure FAILED: proc={procName} timeout={_commandTimeoutSeconds}s error={ex}");
                throw;
            }
        }

        /// <summary>
        /// Perform a server-side INSERT ... SELECT to copy rows from a source schema.table to the target table.
        /// NOTE: This requires the target MySQL server/connection to be able to read the source schema (i.e. same server or accessible schema).
        /// If the source is on a different server, use the InsertRowsAsync streaming approach or LOAD DATA INFILE.
        /// </summary>
        public async Task<int> InsertFromSelectAsync(string table, string columnsCsv, string sourceSchema, string sourceTable, string? whereClause = null, object? parametersObj = null)
        {
            var cols = NormalizeColumns(columnsCsv).ToList();
            if (!cols.Any()) return 0;

            using var c = new MySqlConnection(_conn);
            await c.OpenAsync();
            using var tx = await c.BeginTransactionAsync();

            try
            {
                var colsList = string.Join(", ", cols.Select(cn => $"`{cn}`"));
                var sqlSb = new StringBuilder();
                sqlSb.Append($"INSERT INTO `{_targetSchema}`.`{table}` ({colsList}) ");
                sqlSb.Append($"SELECT {colsList} FROM `{sourceSchema}`.`{sourceTable}`");
                if (!string.IsNullOrWhiteSpace(whereClause)) sqlSb.Append(" WHERE " + whereClause);

                var sql = sqlSb.ToString();
                var sw = Stopwatch.StartNew();
                var affected = await c.ExecuteAsync(sql, parametersObj, tx);
                await tx.CommitAsync();
                sw.Stop();
                Console.WriteLine($"InsertFromSelect: table={table} rows={affected} elapsed={sw.ElapsedMilliseconds}ms");
                return affected;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public void Dispose() { }

        public async Task TruncateTargetAsync(string table)
        {
            using var c = new MySqlConnection(_conn);
            await c.OpenAsync();
            var sql = $"TRUNCATE TABLE `{_targetSchema}`.`{table}`";
            var sw = Stopwatch.StartNew();
            await c.ExecuteAsync(sql, commandTimeout: _commandTimeoutSeconds);
            sw.Stop();
            Console.WriteLine($"TruncateTarget: table={table} elapsed={sw.ElapsedMilliseconds}ms");
        }

        public async Task<int> DeleteByPrimaryKeysAsync(string table, string pkName, IEnumerable<object> pkValues)
        {
            var list = pkValues?.ToList() ?? new List<object>();
            if (!list.Any()) return 0;

            using var c = new MySqlConnection(_conn);
            await c.OpenAsync();

            // Build parameterized IN list
            var parameters = new DynamicParameters();
            var names = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var pname = "@pk" + i;
                names.Add(pname);
                parameters.Add(pname, list[i]);
            }

            var sql = $"DELETE FROM `{_targetSchema}`.`{table}` WHERE `{pkName}` IN ({string.Join(",", names)})";
            var sw = Stopwatch.StartNew();
            try
            {
                var affected = await c.ExecuteAsync(sql, parameters, commandTimeout: _commandTimeoutSeconds);
                sw.Stop();
                Console.WriteLine($"DeleteByPrimaryKeys: table={table} pk={pkName} count={list.Count} elapsed={sw.ElapsedMilliseconds}ms");
                return affected;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"DeleteByPrimaryKeys FAILED: table={table} pk={pkName} count={list.Count} elapsed={sw.ElapsedMilliseconds}ms error={ex}");
                throw;
            }
        }

        public async Task<int> InsertRowsAsync(string table, string columnsCsv, IEnumerable<dynamic> rows)
        {
            var cols = NormalizeColumns(columnsCsv).ToList();
            var rowList = rows?.ToList() ?? new List<dynamic>();
            if (!rowList.Any()) return 0;

            // To avoid exceeding MySQL max_allowed_packet, split into smaller inserts.
            const int maxRowsPerInsert = 250; // conservative default, tune if needed
            var totalInserted = 0;
            var swOverall = Stopwatch.StartNew();

            for (int offset = 0; offset < rowList.Count; offset += maxRowsPerInsert)
            {
                var chunk = rowList.Skip(offset).Take(maxRowsPerInsert).ToList();
                // execute chunk with adaptive retry to handle packet-too-large / connection abort errors
                var inserted = await InsertChunkWithRetryAsync(table, mangle: null, columnsCsv: string.Join(",", cols), chunk: chunk);
                totalInserted += inserted;
            }

            swOverall.Stop();
            // Log adaptive metrics for this table (splits/retries) and reset counters
            LogAndResetMetrics(table);
            Console.WriteLine($"InsertRows: table={table} totalRows={rowList.Count} totalInserted={totalInserted} totalElapsed={swOverall.ElapsedMilliseconds}ms");
            return totalInserted;
        }

        private static IEnumerable<string> NormalizeColumns(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Enumerable.Empty<string>();
            return csv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
        }

        /// <summary>
        /// Bulk insert fallback when LOAD DATA LOCAL INFILE is not available.
        /// This implementation uses multi-row parameterized INSERT statements
        /// with adaptive splitting to avoid max_allowed_packet issues.
        /// Returns number of rows inserted.
        /// </summary>
        public async Task<int> InsertRowsBulkAsync(string table, string columnsCsv, IEnumerable<dynamic> rows)
        {
            var rowList = rows?.ToList() ?? new List<dynamic>();
            if (!rowList.Any()) return 0;

            // Reuse existing InsertRowsAsync implementation which splits into chunks
            // and uses ExecuteInsertChunkAsync/InsertChunkWithRetryAsync for adaptive behavior.
            Console.WriteLine($"InsertRowsBulk (multi-row INSERT fallback): table={table} rows={rowList.Count}");
            var inserted = await InsertRowsAsync(table, columnsCsv, rowList);
            Console.WriteLine($"InsertRowsBulk completed: table={table} inserted={inserted}");
            return inserted;
        }

        // Execute one chunk (list of rows) as an INSERT within its own connection/transaction.
        private async Task<int> ExecuteInsertChunkAsync(string table, List<string> cols, List<dynamic> chunk)
        {
            using var c = new MySqlConnection(_conn);
            await c.OpenAsync();
            using var tx = await c.BeginTransactionAsync();

            var sqlSb = new StringBuilder();
            sqlSb.Append($"INSERT INTO `{_targetSchema}`.`{table}` ({string.Join(", ", cols.Select(cn => $"`{cn}`"))}) VALUES ");

            var parameters = new DynamicParameters();
            var valueGroups = new List<string>();
            int rowIdx = 0;

            foreach (var row in chunk)
            {
                IDictionary<string, object> dict = row as IDictionary<string, object> ?? (IDictionary<string, object>)row;
                var paramNames = new List<string>();
                foreach (var col in cols)
                {
                    var safeCol = Regex.Replace(col, "[^a-zA-Z0-9_]", "_");
                    var paramName = $"@p{rowIdx}_{safeCol}";
                    paramNames.Add(paramName);

                    if (!dict.TryGetValue(col, out var val))
                    {
                        var match = dict.Keys.FirstOrDefault(k => string.Equals(k, col, StringComparison.OrdinalIgnoreCase));
                        val = match != null ? dict[match] : null;
                    }

                    parameters.Add(paramName, val);
                }

                valueGroups.Add("(" + string.Join(",", paramNames) + ")");
                rowIdx++;
            }

            sqlSb.Append(string.Join(",", valueGroups));
            var sql = sqlSb.ToString();

            var sw = Stopwatch.StartNew();
            try
            {
                var inserted = await c.ExecuteAsync(sql, parameters, tx);
                await tx.CommitAsync();
                sw.Stop();
                Console.WriteLine($"ExecuteInsertChunk: table={table} chunkRows={chunk.Count} inserted={inserted} elapsed={sw.ElapsedMilliseconds}ms");
                return inserted;
            }
            catch
            {
                try { await tx.RollbackAsync(); } catch { }
                sw.Stop();
                throw;
            }
        }

        private bool IsPacketOrConnectionError(Exception ex)
        {
            if (ex == null) return false;
            var msg = ex.Message ?? string.Empty;
            if (msg.IndexOf("max_allowed_packet", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("Error submitting", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("connection was aborted", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (ex.InnerException != null) return IsPacketOrConnectionError(ex.InnerException);
            return false;
        }

        // Adaptive insert: try to insert the chunk; on packet/connection errors split and retry halves.
        private async Task<int> InsertChunkWithRetryAsync(string table, string? mangle, string columnsCsv, List<dynamic> chunk)
        {
            var cols = NormalizeColumns(columnsCsv).ToList();
            if (chunk == null || chunk.Count == 0) return 0;
            // Proactively estimate payload size and split if it's likely to exceed the allowed packet size
            try
            {
                long estimated = EstimateChunkSizeBytes(cols, chunk);
                if (estimated > _maxPacketSizeBytes)
                {
                    var mid = chunk.Count / 2;
                    var left = chunk.Take(mid).ToList();
                    var right = chunk.Skip(mid).ToList();
                    IncrementSplitCount(table);
                    Console.WriteLine($"InsertChunkWithRetry: estimated payload {estimated} bytes exceeds threshold {_maxPacketSizeBytes} - splitting {chunk.Count} into {left.Count} and {right.Count}");
                    var leftCount = await InsertChunkWithRetryAsync(table, mangle, columnsCsv, left);
                    var rightCount = await InsertChunkWithRetryAsync(table, mangle, columnsCsv, right);
                    return leftCount + rightCount;
                }

                return await ExecuteInsertChunkAsync(table, cols, chunk);
            }
            catch (Exception ex)
            {
                if (!IsPacketOrConnectionError(ex) || chunk.Count <= 1)
                {
                    Console.WriteLine($"InsertChunkWithRetry: non-retriable or single-row failure: {ex}");
                    throw;
                }

                // split and retry halves if an unexpected packet/connection error occurs
                var mid = chunk.Count / 2;
                var left = chunk.Take(mid).ToList();
                var right = chunk.Skip(mid).ToList();
                IncrementRetryCount(table);
                IncrementSplitCount(table);
                Console.WriteLine($"InsertChunkWithRetry: splitting chunk of {chunk.Count} into {left.Count} and {right.Count} due to error: {ex.Message}");
                var leftCount = await InsertChunkWithRetryAsync(table, mangle, columnsCsv, left);
                var rightCount = await InsertChunkWithRetryAsync(table, mangle, columnsCsv, right);
                return leftCount + rightCount;
            }
        }

        private long EstimateChunkSizeBytes(List<string> cols, List<dynamic> chunk)
        {
            long total = 0;
            // approximate SQL overhead per column per row
            const int overheadPerValue = 8; // commas, quotes, param names approx
            foreach (var row in chunk)
            {
                IDictionary<string, object> dict = row as IDictionary<string, object> ?? (IDictionary<string, object>)row;
                foreach (var col in cols)
                {
                    if (!dict.TryGetValue(col, out var val))
                    {
                        var match = dict.Keys.FirstOrDefault(k => string.Equals(k, col, StringComparison.OrdinalIgnoreCase));
                        val = match != null ? dict[match] : null;
                    }

                    if (val == null) { total += 2 + overheadPerValue; continue; }
                    var s = Convert.ToString(val);
                    if (s == null) { total += 2 + overheadPerValue; continue; }
                    total += Encoding.UTF8.GetByteCount(s) + overheadPerValue;
                }
            }

            // add approximate SQL text overhead for column names and framing
            var colsSqlOver = cols.Sum(c => c.Length + 3);
            total += colsSqlOver + 100; // padding for INSERT keywords, backticks, VALUES, etc.
            return total;
        }

        private void IncrementSplitCount(string table)
        {
            lock (_splitCounts)
            {
                if (_splitCounts.ContainsKey(table)) _splitCounts[table]++;
                else _splitCounts[table] = 1;
            }
        }

        private void IncrementRetryCount(string table)
        {
            lock (_retryCounts)
            {
                if (_retryCounts.ContainsKey(table)) _retryCounts[table]++;
                else _retryCounts[table] = 1;
            }
        }

        private void LogAndResetMetrics(string table)
        {
            int splits = 0, retries = 0;
            lock (_splitCounts) { if (_splitCounts.TryGetValue(table, out var s)) { splits = s; _splitCounts.Remove(table); } }
            lock (_retryCounts) { if (_retryCounts.TryGetValue(table, out var r)) { retries = r; _retryCounts.Remove(table); } }
            if (splits > 0 || retries > 0)
            {
                Console.WriteLine($"Adaptive metrics: table={table} splits={splits} retries={retries}");
            }
        }
    }
}
