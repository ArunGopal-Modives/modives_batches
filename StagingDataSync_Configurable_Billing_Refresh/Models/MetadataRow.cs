namespace Models
{
    public class MetadataRow
    {
        public int Stg_Data_Tables_Id { get; set; }
        public string TableName { get; set; } = string.Empty;
        public string SchemaName { get; set; } = string.Empty;
        public string ColumnsName { get; set; } = string.Empty;
        public string DeltaColumnName { get; set; } = string.Empty;
        public string IsDeltaConfig { get; set; } = string.Empty;
    }
}
