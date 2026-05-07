using Npgsql;
using Dapper;
using System.Data;

namespace RAMSOfficial.Services;

public class DatabaseSchemaService
{
    private readonly string _connectionString;

    public DatabaseSchemaService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<TableSchema>> GetAllTablesAsync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        var tables = await connection.QueryAsync<TableSchema>(@"
            SELECT 
                table_name as TableName,
                table_type as TableType
            FROM information_schema.tables
            WHERE table_schema = 'public'
            ORDER BY table_name
        ");
        return tables.ToList();
    }

    public async Task<List<ColumnSchema>> GetTableColumnsAsync(string tableName)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        var columns = await connection.QueryAsync<ColumnSchema>(@"
            SELECT 
                column_name as ColumnName,
                ordinal_position as OrdinalPosition,
                column_default as ColumnDefault,
                is_nullable as IsNullable,
                data_type as DataType,
                character_maximum_length as CharacterMaximumLength,
                numeric_precision as NumericPrecision,
                numeric_scale as NumericScale,
                udt_name as UdtName
            FROM information_schema.columns
            WHERE table_schema = 'public'
                AND table_name = @TableName
            ORDER BY ordinal_position
        ", new { TableName = tableName });
        return columns.ToList();
    }

    public async Task<string> GenerateCreateTableScriptAsync(string tableName)
    {
        var columns = await GetTableColumnsAsync(tableName);
        var script = $"CREATE TABLE {tableName} (\n";
        
        var columnDefs = columns.Select(c => {
            var type = c.DataType.ToUpper() switch
            {
                "CHARACTER VARYING" => $"VARCHAR({c.CharacterMaximumLength})",
                "INTEGER" => "INTEGER",
                "BIGINT" => "BIGINT",
                "TIMESTAMP WITHOUT TIME ZONE" => "TIMESTAMP",
                "TIMESTAMP WITH TIME ZONE" => "TIMESTAMPTZ",
                "BOOLEAN" => "BOOLEAN",
                "TEXT" => "TEXT",
                "DATE" => "DATE",
                "NUMERIC" => $"NUMERIC({c.NumericPrecision},{c.NumericScale})",
                _ => c.DataType.ToUpper()
            };
            
            var nullable = c.IsNullable == "NO" ? " NOT NULL" : "";
            var defaultVal = !string.IsNullOrEmpty(c.ColumnDefault) ? $" DEFAULT {c.ColumnDefault}" : "";
            
            return $"    {c.ColumnName} {type}{nullable}{defaultVal}";
        });
        
        script += string.Join(",\n", columnDefs);
        script += "\n);";
        
        return script;
    }

    public async Task<Dictionary<string, object>> GetDatabaseInfoAsync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var info = new Dictionary<string, object>();

        // Get all tables
        var tables = await GetAllTablesAsync();
        info["Tables"] = tables;

        // Get columns for each table
        var allColumns = new Dictionary<string, List<ColumnSchema>>();
        foreach (var table in tables)
        {
            allColumns[table.TableName] = await GetTableColumnsAsync(table.TableName);
        }
        info["Columns"] = allColumns;

        // Get row counts
        var rowCounts = await connection.QueryAsync<TableRowCount>(@"
            SELECT 
                relname as TableName,
                n_live_tup as RowCount
            FROM pg_stat_user_tables
            WHERE schemaname = 'public'
            ORDER BY relname
        ");
        info["RowCounts"] = rowCounts.ToList();

        return info;
    }

    public async Task SyncDatabaseSchemaAsync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Get existing table structure
        var tables = await GetAllTablesAsync();
        
        foreach (var table in tables)
        {
            var columns = await GetTableColumnsAsync(table.TableName);
            Console.WriteLine($"\nTable: {table.TableName}");
            foreach (var col in columns)
            {
                Console.WriteLine($"  {col.ColumnName} - {col.DataType} - Nullable: {col.IsNullable}");
            }
        }
    }
}

public class TableSchema
{
    public string TableName { get; set; } = string.Empty;
    public string TableType { get; set; } = string.Empty;
}

public class ColumnSchema
{
    public string ColumnName { get; set; } = string.Empty;
    public int OrdinalPosition { get; set; }
    public string? ColumnDefault { get; set; }
    public string IsNullable { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int? CharacterMaximumLength { get; set; }
    public int? NumericPrecision { get; set; }
    public int? NumericScale { get; set; }
    public string UdtName { get; set; } = string.Empty;
}

public class TableRowCount
{
    public string TableName { get; set; } = string.Empty;
    public long RowCount { get; set; }
}
