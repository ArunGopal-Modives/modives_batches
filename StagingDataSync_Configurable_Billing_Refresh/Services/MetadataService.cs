using Dapper;
using MySqlConnector;
using System.Data;
using Models;

namespace Services
{
    public class MetadataService
    {
        private readonly string _conn;
        private readonly string _metadataSchema;

        public MetadataService(string connectionString, string metadataSchema)
        {
            _conn = connectionString;
            _metadataSchema = metadataSchema;
        }

        public async Task<IEnumerable<MetadataRow>> GetAllMetadataAsync()
        {
            using var c = new MySqlConnection(_conn);
            var sql = $"SELECT Stg_Data_Tables_Id, TableName, SchemaName, ColumnsName, DeltaColumnName, IsDeltaConfig FROM `{_metadataSchema}`.stg_data_tables";
            return await c.QueryAsync<MetadataRow>(sql);
        }

        public async Task<DateTime> GetLastSuccessfulLoadAsync(string stgTableName)
        {
            using var c = new MySqlConnection(_conn);
            var sql = $"SELECT IFNULL(LastUpdatedTs, '1900-01-01') FROM `{_metadataSchema}`.stg_table_control WHERE TableName = @TableName AND `status` = 1 ORDER BY LastUpdatedTs DESC LIMIT 1";
            var val = await c.ExecuteScalarAsync<object?>(sql, new { TableName = stgTableName });
            if (val == null) return DateTime.SpecifyKind(DateTime.Parse("1900-01-01"), DateTimeKind.Utc);

            // val can be DateTime, DateTimeOffset, unix seconds (long), or string representation.
            if (val is DateTime dt)
            {
                // Normalize to UTC
                var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }

            if (val is DateTimeOffset dto)
            {
                return dto.UtcDateTime;
            }

            var s = Convert.ToString(val);
            if (long.TryParse(s, out var l))
            {
                // assume unix seconds
                return DateTimeOffset.FromUnixTimeSeconds(l).UtcDateTime;
            }

            if (DateTimeOffset.TryParse(s, out var parsedDto))
            {
                return parsedDto.UtcDateTime;
            }

            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            // fallback
            return DateTime.SpecifyKind(DateTime.Parse("1900-01-01"), DateTimeKind.Utc);
        }

        public async Task UpsertControlAsync(string stgTableName, string schemaName, int status, DateTime lastUpdatedTs)
        {
            using var c = new MySqlConnection(_conn);
            var sql = $@"
INSERT INTO `{_metadataSchema}`.stg_table_control (TableName, SchemaName, `status`, LastUpdatedTs)
VALUES (@TableName, @SchemaName, @Status, @LastUpdatedTs)
ON DUPLICATE KEY UPDATE `status` = @Status, LastUpdatedTs = @LastUpdatedTs;";

            // Ensure timestamp is UTC and pass explicit DbType to avoid ambiguity
            var tsUtc = DateTime.SpecifyKind(lastUpdatedTs.Kind == DateTimeKind.Utc ? lastUpdatedTs : lastUpdatedTs.ToUniversalTime(), DateTimeKind.Utc);
            var parameters = new DynamicParameters();
            parameters.Add("TableName", stgTableName);
            parameters.Add("SchemaName", schemaName);
            parameters.Add("Status", status);
            parameters.Add("LastUpdatedTs", tsUtc, DbType.DateTime);

            await c.ExecuteAsync(sql, parameters);
        }

        public async Task MarkIsDeltaAsync(string tableName, string schemaName)
        {
            using var c = new MySqlConnection(_conn);
            var sql = $@"UPDATE `{_metadataSchema}`.stg_data_tables SET IsDeltaConfig = 'Y' WHERE TableName = @TableName AND SchemaName = @SchemaName";
            await c.ExecuteAsync(sql, new { TableName = tableName, SchemaName = schemaName });
        }
    }
}
