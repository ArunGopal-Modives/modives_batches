using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using MySqlConnector;

namespace Services
{
    /// <summary>
    /// Reloads `modives-analytics`.dealer_contracts (DealerId, ContractName,
    /// ClientName) from the contracts Excel sheet.
    ///
    /// Controlled by App.config:
    ///   RefreshDealerContracts    = Y to run, anything else to skip
    ///   DealerContractsFilePath   = full path to the .xlsx file
    ///   DealerContractsSheetName  = optional; first sheet is used when blank
    ///
    /// DELETE and INSERT run in one transaction. If anything fails, the
    /// previous dealer_contracts data is preserved.
    /// </summary>
    public sealed class DealerContractsRefreshService
    {
        // Header names are matched case-insensitively with spaces/line breaks removed,
        // so "Contract Name", "ContractName" and a wrapped "Contract\nName" all match.
        private const string DealerIdHeader = "dealerid";
        private const string ContractNameHeader = "contractname";
        private const string ClientNameHeader = "clientname";

        private readonly string _connectionString;
        private readonly int _commandTimeoutSeconds;
        private readonly string _filePath;
        private readonly string? _sheetName;

        public DealerContractsRefreshService(string connectionString)
        {
            _connectionString = connectionString;

            var env = Environment.GetEnvironmentVariable("COMMAND_TIMEOUT_SECONDS");
            _commandTimeoutSeconds = int.TryParse(env, out var timeout) && timeout > 0
                ? timeout
                : 7200;

            _filePath = System.Configuration.ConfigurationManager.AppSettings["DealerContractsFilePath"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                throw new InvalidOperationException(
                    "DealerContractsFilePath must be configured in App.config when RefreshDealerContracts=Y. " +
                    "Example: <add key=\"DealerContractsFilePath\" value=\"C:\\Data\\Contracts.xlsx\" />");
            }

            // A relative path is resolved against the folder the utility runs from.
            _filePath = Path.GetFullPath(_filePath.Trim(), AppContext.BaseDirectory);

            var sheetName = System.Configuration.ConfigurationManager.AppSettings["DealerContractsSheetName"];
            _sheetName = string.IsNullOrWhiteSpace(sheetName) ? null : sheetName.Trim();
        }

        public static bool IsEnabled()
        {
            var flag = System.Configuration.ConfigurationManager.AppSettings["RefreshDealerContracts"];
            return string.Equals(flag?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<int> RefreshAsync()
        {
            AppLogger.Info($"DEALER_CONTRACTS: Reading contracts sheet. File={_filePath}, Sheet={_sheetName ?? "<first sheet>"}.");

            var rows = ReadSheet();

            if (rows.Count == 0)
            {
                // Never wipe the table because of an empty or wrongly selected sheet.
                throw new InvalidOperationException(
                    $"DEALER_CONTRACTS: No rows with a DealerId were found in {_filePath}. dealer_contracts was not changed.");
            }

            var duplicates = rows
                .GroupBy(r => r.DealerId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                AppLogger.Warning(
                    $"DEALER_CONTRACTS: {duplicates.Count} DealerId value(s) appear more than once in the sheet: " +
                    $"{string.Join(", ", duplicates.Take(20))}{(duplicates.Count > 20 ? ", ..." : "")}. All rows will be loaded.");
            }

            AppLogger.Info($"DEALER_CONTRACTS: Read {rows.Count} rows from the sheet. Reloading dealer_contracts.");

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

                // DELETE instead of TRUNCATE: TRUNCATE commits implicitly and cannot be rolled back.
                var deleted = await connection.ExecuteAsync(
                    "DELETE FROM `modives-analytics`.dealer_contracts;",
                    transaction: transaction,
                    commandTimeout: _commandTimeoutSeconds);

                // One timestamp for the whole reload, stored in dealer_contracts.load_date.
                var loadDate = DateTime.Now;
                AppLogger.Info($"DEALER_CONTRACTS: load_date for this run = {loadDate:yyyy-MM-dd HH:mm:ss}.");

                var inserted = await connection.ExecuteAsync(
                    @"INSERT INTO `modives-analytics`.dealer_contracts (DealerId, ContractName, ClientName, load_date)
VALUES (@DealerId, @ContractName, @ClientName, @LoadDate);",
                    rows.Select(r => new { r.DealerId, r.ContractName, r.ClientName, LoadDate = loadDate }),
                    transaction: transaction,
                    commandTimeout: _commandTimeoutSeconds);

                await transaction.CommitAsync();

                AppLogger.Success(
                    $"DEALER_CONTRACTS: Completed successfully. Deleted={deleted}, Inserted={inserted}.");

                return inserted;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "DEALER_CONTRACTS: Reload failed. DELETE will be rolled back.",
                    ex);

                try
                {
                    await transaction.RollbackAsync();
                    AppLogger.Warning(
                        "DEALER_CONTRACTS: Transaction rolled back successfully. " +
                        "The previous dealer_contracts data was preserved.");
                }
                catch (Exception rollbackEx)
                {
                    AppLogger.Error(
                        "DEALER_CONTRACTS: CRITICAL - transaction rollback failed. " +
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
                        "DEALER_CONTRACTS: Failed to restore SQL_SAFE_UPDATES=1.",
                        safeUpdateEx);
                }
            }
        }

        private List<DealerContractRow> ReadSheet()
        {
            if (!File.Exists(_filePath))
            {
                throw new FileNotFoundException(
                    $"Dealer contracts sheet was not found. Expected path: {_filePath}",
                    _filePath);
            }

            // FileShare.ReadWrite lets the file be read while it is open in Excel.
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);

            IXLWorksheet sheet;
            if (_sheetName == null)
            {
                sheet = workbook.Worksheet(1);
            }
            else if (!workbook.TryGetWorksheet(_sheetName, out sheet))
            {
                throw new InvalidOperationException(
                    $"Sheet '{_sheetName}' was not found in {_filePath}. " +
                    $"Available sheets: {string.Join(", ", workbook.Worksheets.Select(w => w.Name))}.");
            }

            var headerRow = sheet.FirstRowUsed()
                ?? throw new InvalidOperationException($"Sheet '{sheet.Name}' in {_filePath} is empty.");

            var dealerIdCol = FindColumn(headerRow, DealerIdHeader);
            var contractNameCol = FindColumn(headerRow, ContractNameHeader);
            var clientNameCol = FindColumn(headerRow, ClientNameHeader);

            AppLogger.Info(
                $"DEALER_CONTRACTS: Using sheet '{sheet.Name}'. Columns: DealerId={dealerIdCol}, " +
                $"ContractName={contractNameCol}, ClientName={clientNameCol} (1 = column A).");

            var rows = new List<DealerContractRow>();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();

            for (var r = headerRow.RowNumber() + 1; r <= lastRow; r++)
            {
                var dealerId = CellText(sheet.Cell(r, dealerIdCol));
                if (dealerId == null)
                {
                    continue;
                }

                rows.Add(new DealerContractRow(
                    dealerId,
                    CellText(sheet.Cell(r, contractNameCol)),
                    CellText(sheet.Cell(r, clientNameCol))));
            }

            return rows;
        }

        private int FindColumn(IXLRow headerRow, string normalizedHeader)
        {
            foreach (var cell in headerRow.CellsUsed())
            {
                if (Normalize(cell.GetString()) == normalizedHeader)
                {
                    return cell.Address.ColumnNumber;
                }
            }

            throw new InvalidOperationException(
                $"Column '{normalizedHeader}' was not found in the header row of {_filePath}. " +
                $"Headers found: {string.Join(" | ", headerRow.CellsUsed().Select(c => c.GetString()))}.");
        }

        private static string Normalize(string header) =>
            Regex.Replace(header ?? string.Empty, @"\s+", string.Empty).ToLowerInvariant();

        private static string? CellText(IXLCell cell)
        {
            // Formula errors such as #N/A are loaded as NULL.
            if (cell.IsEmpty() || cell.DataType == XLDataType.Error)
            {
                return null;
            }

            // Whole numbers (e.g. DealerId 23) are stored as doubles in Excel; avoid "23.0".
            if (cell.DataType == XLDataType.Number)
            {
                var number = cell.GetDouble();
                return number == Math.Floor(number)
                    ? ((long)number).ToString(CultureInfo.InvariantCulture)
                    : number.ToString(CultureInfo.InvariantCulture);
            }

            var text = cell.GetFormattedString().Trim();
            return text.Length == 0 ? null : text;
        }

        private sealed record DealerContractRow(string DealerId, string? ContractName, string? ClientName);
    }
}
