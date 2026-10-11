using Services;
using System.Text.RegularExpressions;

// csharp
string sourceConn =
   BuildMySqlConnFromAppConfig("Source")
    ?? throw new InvalidOperationException("SOURCE_CONN not set");

string targetConn =
     BuildMySqlConnFromAppConfig("Target")
    ?? throw new InvalidOperationException("TARGET_CONN not set");

string metadataSchema = "modives-analytics";
string targetSchema = metadataSchema;
int batchSize = 1000;

static string BuildMySqlConnFromAppConfig(string prefix)
{
    // Read from legacy app.config <appSettings>
    var server = System.Configuration.ConfigurationManager.AppSettings[$"{prefix}Server"] ?? System.Configuration.ConfigurationManager.AppSettings[$"{prefix}server"];
    if (string.IsNullOrWhiteSpace(server)) return null;
    var port = System.Configuration.ConfigurationManager.AppSettings[$"{prefix}Port"] ?? System.Configuration.ConfigurationManager.AppSettings[$"{prefix}port"] ?? "3306";
    var database = System.Configuration.ConfigurationManager.AppSettings[$"{prefix}Database"] ?? System.Configuration.ConfigurationManager.AppSettings[$"{prefix}database"];
    var user = System.Configuration.ConfigurationManager.AppSettings[$"{prefix}User"] ?? System.Configuration.ConfigurationManager.AppSettings[$"{prefix}user"];
    var pwd = System.Configuration.ConfigurationManager.AppSettings[$"{prefix}Password"] ?? System.Configuration.ConfigurationManager.AppSettings[$"{prefix}password"];

    if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user)) return null;
    return $"Server={server};Port={port};Database={database};Uid={user};Pwd={pwd};" +
       $"ConnectionTimeout=60;DefaultCommandTimeout=7200;Keepalive=60;";
}

Console.WriteLine($"Source: {RedactConn(sourceConn)}");
Console.WriteLine($"Target: {RedactConn(targetConn)}");
Console.WriteLine($"Metadata schema: {metadataSchema}, Target schema: {targetSchema}, BatchSize: {batchSize}"); 

// Reload dealer_contracts from the contracts sheet first, so billing_transactions
// uses the latest contract names. A failure stops the run; the reload is
// transactional, so the previous dealer_contracts data is preserved.
if (DealerContractsRefreshService.IsEnabled())
{
    try
    {
        AppLogger.Info("DEALER_CONTRACTS: RefreshDealerContracts=Y. Starting dealer_contracts reload.");
        var dealerContractsService = new DealerContractsRefreshService(targetConn);
        await dealerContractsService.RefreshAsync();
    }
    catch (Exception ex)
    {
        AppLogger.Error("STAGING_RUN: dealer_contracts reload failed. The run was stopped. ExitCode=1.", ex);
        Environment.ExitCode = 1;
        return;
    }
}
else
{
    AppLogger.Info("DEALER_CONTRACTS: RefreshDealerContracts is not Y. dealer_contracts reload was skipped.");
}

var metadataService = new Services.MetadataService(targetConn, metadataSchema);
var sourceReader = new Services.SourceReader(sourceConn);
var targetWriter = new Services.TargetWriter(targetConn, targetSchema);

IEnumerable<Models.MetadataRow> metas;
try
{
    metas = await metadataService.GetAllMetadataAsync();
}
catch (Exception ex)
{
    AppLogger.Error("STAGING_RUN: Failed to read staging metadata. The run cannot continue.", ex);
    throw;
}

// If any table insert/refresh happens during processing we should run
// post-refresh stored procedures once after all tables are processed.
var needPostRefresh = false;
var stagingRefreshSucceeded = false;
var billingRefreshFailed = false;
AppLogger.Info("STAGING_RUN: Starting staging data synchronization.");

foreach (var m in metas)
{
    Console.WriteLine($"Processing {m.SchemaName}.{m.TableName} (IsDelta={m.IsDeltaConfig})");
    AppLogger.Info($"STAGING_TABLE: Starting {m.SchemaName}.{m.TableName} (IsDelta={m.IsDeltaConfig}).");
    try
    {
        var stgName = "stg_" + m.TableName;
        var lastLoad = await metadataService.GetLastSuccessfulLoadAsync(stgName);

        if (m.IsDeltaConfig?.ToUpperInvariant() == "N")
        {
            // Full initial load: prefer server-side copy when source and target are on same server,
            // otherwise use bulk load per batch with fallback to multi-row inserts.
            Console.WriteLine("Full load detected. Doing full table copy.");

            // If same server/host, do INSERT ... SELECT to avoid client transfer
            string GetServer(string conn)
            {
                if (string.IsNullOrEmpty(conn)) return string.Empty;
                var mSrv = System.Text.RegularExpressions.Regex.Match(conn, "Server=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return mSrv.Success ? mSrv.Groups[1].Value.Trim().ToLowerInvariant() : string.Empty;
            }

            var srcHost = GetServer(sourceConn);
            var tgtHost = GetServer(targetConn);

            await targetWriter.TruncateTargetAsync(stgName);

            if (!string.IsNullOrEmpty(srcHost) && srcHost == tgtHost)
            {
                // server-side copy
                try
                {
                    var affected = await targetWriter.InsertFromSelectAsync(stgName, m.ColumnsName, m.SchemaName, m.TableName);
                    if (affected > 0)
                    {
                        await metadataService.MarkIsDeltaAsync(m.TableName, m.SchemaName);
                        await metadataService.UpsertControlAsync(stgName, targetSchema, 1, DateTime.UtcNow);
                        // Mark that we need to run post-refresh stored procedures
                        // after all tables are processed (do not call them per-table).
                        needPostRefresh = true;
                        stagingRefreshSucceeded = true;
                        AppLogger.Success($"STAGING_TABLE: Successfully refreshed {stgName}.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"InsertFromSelect failed for {m.TableName}: {ex}");
                    throw;
                }
            }
            else
            {
                // client-side: stream rows and load in bulk per batch with fallback
                var rows = await sourceReader.QueryRowsAsync(m.SchemaName, m.TableName, m.ColumnsName, null, batchSize);
                int inserted = 0;
                foreach (var batch in Batch(rows, batchSize))
                {
                    try
                    {
                        // try bulk load first
                        inserted += await targetWriter.InsertRowsBulkAsync(stgName, m.ColumnsName, batch);
                    }
                    catch (Exception bulkEx)
                    {
                        Console.WriteLine($"Bulk load failed for {stgName}: {bulkEx}. Falling back to InsertRowsAsync for this batch.");
                        inserted += await targetWriter.InsertRowsAsync(stgName, m.ColumnsName, batch);
                    }
                }

                if (inserted > 0)
                {
                    await metadataService.MarkIsDeltaAsync(m.TableName, m.SchemaName);
                    await metadataService.UpsertControlAsync(stgName, targetSchema, 1, DateTime.UtcNow);
                    // Ensure post-refresh stored procedures are run after all tables
                    // when client-side inserts occurred.
                    needPostRefresh = true;
                    stagingRefreshSucceeded = true;
                    AppLogger.Success($"STAGING_TABLE: Successfully refreshed {stgName}.");
                }
            }
        }
        else
        {
            // Delta processing: delete modified PKs from stg and insert changed/new rows
            if (string.IsNullOrWhiteSpace(m.DeltaColumnName))
            {
                Console.WriteLine("Delta column not defined; skipping.");
                continue;
            }

            var modifiedRows = await sourceReader.QueryRowsAsync(m.SchemaName, m.TableName, m.ColumnsName, (m.DeltaColumnName, lastLoad), batchSize);
            var modifiedList = modifiedRows.ToList();
            if (!modifiedList.Any())
            {
                Console.WriteLine("No changes found since last load.");
                continue;
            }

            var pk = await sourceReader.GetPrimaryKeyColumnAsync(m.SchemaName, m.TableName);
            if (pk == null)
            {
                Console.WriteLine("Primary key not discovered; skipping delta for this table.");
                continue;
            }

            // Collect PK values
            var pkValues = modifiedList.Select(r => ((IDictionary<string, object>)r)[pk]).ToList();

            // Delete existing rows in target for those PKs
            await targetWriter.DeleteByPrimaryKeysAsync(stgName, pk, pkValues);

            int inserted = 0;
            foreach (var batch in Batch(modifiedList, batchSize))
            {
                inserted += await targetWriter.InsertRowsAsync(stgName, m.ColumnsName, batch);
            }

            // Determine last processed delta ts
            var maxDelta = modifiedList
                .Select(r => ((IDictionary<string, object>)r)[m.DeltaColumnName])
                .Where(x => x != null)
                .Select(obj => ConvertToDateTime(obj))
                .OrderByDescending(d => d)
                .FirstOrDefault();

            var lastTs = maxDelta == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind(maxDelta.ToUniversalTime(), DateTimeKind.Utc);
            await metadataService.UpsertControlAsync(stgName, targetSchema, 1, lastTs);
            // Defer post-refresh stored procedures until after all tables
            // have been processed.
            needPostRefresh = true;
            stagingRefreshSucceeded = true;
            AppLogger.Success($"STAGING_TABLE: Successfully refreshed {stgName}.");
        }
    }
    catch (Exception ex)
    {
        // Print full exception including stack trace to aid diagnosis of connection/SQL errors
        Console.WriteLine($"Error processing {m.TableName}: {ex}");
        AppLogger.Error($"STAGING_TABLE: Failed processing {m.SchemaName}.{m.TableName}. Staging table was not marked successful.", ex);
        var stgName = "stg_" + m.TableName;
        try
        {
            await metadataService.UpsertControlAsync(stgName, targetSchema, 0, DateTime.UtcNow);
        }
        catch (Exception metaEx)
        {
            Console.WriteLine($"Failed to update control table for {stgName}: {metaEx}");
            AppLogger.Error($"STAGING_TABLE: Failed to update control metadata for {stgName}.", metaEx);
        }
    }
}

if (needPostRefresh)
{
    try
    {
        Console.WriteLine("Running post-refresh stored procedures...");
        AppLogger.Info("STAGING_RUN: Staging refresh completed. Post-refresh stored procedures are not enabled.");
        //await targetWriter.CallStoredProcedureAsync("load_dimensions");
        //await targetWriter.CallStoredProcedureAsync("load_fact_quote");
        //await targetWriter.CallStoredProcedureAsync("load_fact_verification");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Post-refresh stored procedures failed: {ex}");
        AppLogger.Error("POST_REFRESH: Stored procedure execution failed.", ex);
    }
}

// Refresh billing_transactions only after at least one staging table was
// successfully refreshed. DELETE + INSERT are transactional; an INSERT
// failure rolls back the DELETE and preserves the previous billing data.
if (stagingRefreshSucceeded)
{
    try
    {
        AppLogger.Info("BILLING_REFRESH: At least one staging table refreshed successfully. Starting billing_transactions refresh.");
        var billingRefreshService = new Services.BillingTransactionRefreshService(targetConn, targetSchema);
        var inserted = await billingRefreshService.RefreshAsync();
        AppLogger.Success($"BILLING_REFRESH: billing_transactions refresh completed. Inserted={inserted}.");
    }
    catch (Exception ex)
    {
        // The billing service already performs rollback and detailed logging.
        // Keep the staging run itself visible as failed at the billing stage.
        AppLogger.Error("BILLING_REFRESH: billing_transactions refresh failed after staging refresh.", ex);
        Console.WriteLine($"Billing transaction refresh failed: {ex}");
        billingRefreshFailed = true;
    }
}
else
{
    AppLogger.Warning("BILLING_REFRESH: No staging table was successfully refreshed. billing_transactions refresh was skipped.");
}

AppLogger.Info($"STAGING_RUN: Completed. StagingRefreshSucceeded={stagingRefreshSucceeded}, BillingRefreshFailed={billingRefreshFailed}.");
if (billingRefreshFailed)
{
    Environment.ExitCode = 1;
    AppLogger.Error("STAGING_RUN: Run completed with billing_transactions refresh failure. ExitCode=1.");
}

Console.WriteLine("Done.");

static string RedactConn(string conn)
{
    if (string.IsNullOrEmpty(conn)) return "<null>";
    // simple redact: remove password
    return Regex.Replace(conn, "(?i)password=\\\"?[^;\\\"]+\\\"?;?", "password=****;", RegexOptions.IgnoreCase);
}

static DateTime ConvertToDateTime(object obj)
{
    if (obj is DateTime dt) return dt;
    if (obj is string s && DateTime.TryParse(s, out var r)) return r;
    if (long.TryParse(Convert.ToString(obj), out var l)) return DateTimeOffset.FromUnixTimeSeconds(l).UtcDateTime;
    return default;
}

static IEnumerable<IEnumerable<dynamic>> Batch(IEnumerable<dynamic> source, int size)
{
    var batch = new List<dynamic>(size);
    foreach (var item in source)
    {
        batch.Add(item);
        if (batch.Count >= size)
        {
            yield return batch;
            batch = new List<dynamic>(size);
        }
    }
    if (batch.Any()) yield return batch;
}
