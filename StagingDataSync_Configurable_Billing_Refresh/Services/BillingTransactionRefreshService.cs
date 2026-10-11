using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace Services
{
    /// <summary>
    /// Refreshes billing_transactions after at least one staging table has
    /// successfully refreshed.
    ///
    /// DELETE and INSERT are executed in the same transaction. If the INSERT
    /// fails, the DELETE is rolled back so the existing billing data remains.
    /// </summary>
    public sealed class BillingTransactionRefreshService
    {
        private readonly string _connectionString;
        private readonly string _targetSchema;
        private readonly int _commandTimeoutSeconds;
        private readonly int _billingRefreshDays;

        public BillingTransactionRefreshService(string connectionString, string targetSchema)
        {
            _connectionString = connectionString;
            _targetSchema = targetSchema;

            var env = Environment.GetEnvironmentVariable("COMMAND_TIMEOUT_SECONDS");
            _commandTimeoutSeconds = int.TryParse(env, out var timeout) && timeout > 0
                ? timeout
                : 7200;

            var configuredDays = System.Configuration.ConfigurationManager.AppSettings["BillingRefreshDays"];
            if (!int.TryParse(configuredDays, out var refreshDays) || refreshDays < 1)
            {
                throw new InvalidOperationException(
                    "BillingRefreshDays must be configured as a positive integer in App.config. " +
                    "Example: <add key=\"BillingRefreshDays\" value=\"2\" />");
            }

            _billingRefreshDays = refreshDays;
        }

        public async Task<int> RefreshAsync()
        {
            var sqlPath = Path.Combine(
                AppContext.BaseDirectory,
                "Sql",
                "billing_transactions_refresh.sql");

            if (!File.Exists(sqlPath))
            {
                throw new FileNotFoundException(
                    $"Billing refresh SQL file was not found. Expected path: {sqlPath}",
                    sqlPath);
            }

            var insertSql = await File.ReadAllTextAsync(sqlPath);

            if (string.IsNullOrWhiteSpace(insertSql))
            {
                throw new InvalidOperationException(
                    $"Billing refresh SQL file is empty: {sqlPath}");
            }

            const string deleteSql = @"
DELETE FROM `modives-analytics`.billing_transactions
WHERE DATE(VerificationDate) >= DATE_SUB(CURDATE(), INTERVAL @BillingRefreshDays DAY);";

            AppLogger.Info("BILLING_REFRESH: Starting billing_transactions refresh.");
            AppLogger.Info($"BILLING_REFRESH: BillingRefreshDays={_billingRefreshDays}. " +
                           "The same configured window is used for DELETE and INSERT.");

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Required because some MySQL sessions have SQL_SAFE_UPDATES enabled.
                await connection.ExecuteAsync(
                    "SET SQL_SAFE_UPDATES = 0;",
                    transaction: transaction,
                    commandTimeout: _commandTimeoutSeconds);

                // One timestamp for the whole run, stored in billing_transactions.load_date.
                var loadDate = DateTime.Now;
                AppLogger.Info($"BILLING_REFRESH: load_date for this run = {loadDate:yyyy-MM-dd HH:mm:ss}.");

                var parameters = new { BillingRefreshDays = _billingRefreshDays, LoadDate = loadDate };

                var deleted = await connection.ExecuteAsync(
                    deleteSql,
                    parameters,
                    transaction: transaction,
                    commandTimeout: _commandTimeoutSeconds);

                AppLogger.Info(
                    $"BILLING_REFRESH: Deleted {deleted} existing billing_transactions rows for the last {_billingRefreshDays} days.");

                var inserted = await connection.ExecuteAsync(
                    insertSql,
                    parameters,
                    transaction: transaction,
                    commandTimeout: _commandTimeoutSeconds);

                await transaction.CommitAsync();

                AppLogger.Success(
                    $"BILLING_REFRESH: Completed successfully. Deleted={deleted}, Inserted={inserted}.");

                return inserted;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "BILLING_REFRESH: INSERT/refresh failed. DELETE will be rolled back. " +
                    "Review the exception below for the exact SQL/MySQL failure.",
                    ex);

                try
                {
                    await transaction.RollbackAsync();
                    AppLogger.Warning(
                        "BILLING_REFRESH: Transaction rolled back successfully. " +
                        "The previous billing_transactions data was preserved.");
                }
                catch (Exception rollbackEx)
                {
                    AppLogger.Error(
                        "BILLING_REFRESH: CRITICAL - transaction rollback failed. " +
                        "The database transaction state must be checked.",
                        rollbackEx);
                }

                throw;
            }
            finally
            {
                // Restore the session setting regardless of success/failure.
                try
                {
                    await connection.ExecuteAsync(
                        "SET SQL_SAFE_UPDATES = 1;",
                        commandTimeout: _commandTimeoutSeconds);
                }
                catch (Exception safeUpdateEx)
                {
                    AppLogger.Error(
                        "BILLING_REFRESH: Failed to restore SQL_SAFE_UPDATES=1.",
                        safeUpdateEx);
                }
            }
        }
    }
}
