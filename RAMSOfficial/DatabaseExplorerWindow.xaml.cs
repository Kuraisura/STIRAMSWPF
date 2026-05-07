using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;

namespace RAMSOfficial;

public partial class DatabaseExplorerWindow : Window
{
    private sealed class BackupFileItem
    {
        public required string FileName { get; init; }
        public required string LastModified { get; init; }
        public required string Size { get; init; }
        public required string FullPath { get; init; }
    }

    private readonly string _dbPath;
    private readonly string _connectionString;
    private string _currentTable = string.Empty;
    private bool _hasRowId;
    private HashSet<string> _blobColumns = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _primaryKeyColumns = [];

    public DatabaseExplorerWindow(string dbPath, string title)
    {
        InitializeComponent();
        _dbPath = dbPath;
        _connectionString = $"Data Source={_dbPath}";
        Title = $"Database Explorer - {title}";
        HeaderText.Text = $"{title}  |  {_dbPath}\n{_connectionString}";
        Loaded += async (_, _) =>
        {
            await LoadTablesAsync();
            LoadBackups();
        };
    }

    private async Task LoadTablesAsync()
    {
        if (!File.Exists(_dbPath))
        {
            MessageBox.Show($"Database not found:\n{_dbPath}", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        using var reader = await cmd.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        TablesListBox.ItemsSource = tables;
        if (tables.Count > 0)
            TablesListBox.SelectedIndex = 0;
    }

    private async void TablesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TablesListBox.SelectedItem is not string table || string.IsNullOrWhiteSpace(table))
            return;

        _currentTable = table;

        await LoadSchemaAsync(table);
        await LoadDataAsync(table);
    }

    private async Task LoadSchemaAsync(string table)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var dt = new DataTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table}]);";
        using var reader = await cmd.ExecuteReaderAsync();

        for (int i = 0; i < reader.FieldCount; i++)
            dt.Columns.Add(reader.GetName(i), typeof(string));

        while (await reader.ReadAsync())
        {
            var row = dt.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = FormatForGrid(reader, i);
            dt.Rows.Add(row);
        }

        _blobColumns = dt.Rows
            .Cast<DataRow>()
            .Where(r => ((r["type"]?.ToString()) ?? string.Empty).Contains("BLOB", StringComparison.OrdinalIgnoreCase))
            .Select(r => r["name"]?.ToString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _primaryKeyColumns = dt.Rows
            .Cast<DataRow>()
            .Where(r => int.TryParse(r["pk"]?.ToString(), out var pk) && pk > 0)
            .OrderBy(r => int.TryParse(r["pk"]?.ToString(), out var pk) ? pk : 0)
            .Select(r => r["name"]?.ToString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        SchemaGrid.ItemsSource = dt.DefaultView;
    }

    private async Task LoadDataAsync(string table)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var dt = new DataTable();
        SqliteDataReader reader;

        // Prefer rowid for robust update/delete targeting.
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT rowid AS __rowid__, * FROM [{table}] LIMIT 200;";
        try
        {
            reader = await cmd.ExecuteReaderAsync();
            _hasRowId = true;
            DataHintText.Text = "Edit cells then click Save Selected Row. (RowID mode)";
        }
        catch
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM [{table}] LIMIT 200;";
            reader = await cmd.ExecuteReaderAsync();
            _hasRowId = false;
            DataHintText.Text = _primaryKeyColumns.Count > 0
                ? "Edit cells then click Save Selected Row. (Primary-key mode)"
                : "Edit/Delete disabled for this table (no RowID or primary key).";
        }

        using (reader)
        {
            dt.Columns.Add("__selected__", typeof(bool));

            for (int i = 0; i < reader.FieldCount; i++)
                dt.Columns.Add(reader.GetName(i), typeof(string));

            while (await reader.ReadAsync())
            {
                var row = dt.NewRow();
                row["__selected__"] = false;
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = FormatForGrid(reader, i);
                dt.Rows.Add(row);
            }
        }

        DataGridView.ItemsSource = dt.DefaultView;
    }

    private void DataGridView_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (string.Equals(e.PropertyName, "__selected__", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Column is DataGridCheckBoxColumn cb)
            {
                cb.Header = "Select";
                cb.Width = 70;
            }
            return;
        }

        if (string.Equals(e.PropertyName, "__rowid__", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            return;
        }

        if (_blobColumns.Contains(e.PropertyName))
        {
            e.Column.IsReadOnly = true;
            e.Column.Header = $"{e.PropertyName} (BLOB)";
        }
    }

    private async void SaveSelectedRow_Click(object sender, RoutedEventArgs e)
    {
        if (DataGridView.SelectedItem is not DataRowView selected || string.IsNullOrWhiteSpace(_currentTable))
        {
            MessageBox.Show("Select a row first.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_hasRowId && _primaryKeyColumns.Count == 0)
        {
            MessageBox.Show("This table cannot be updated (no RowID or primary key).", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var row = selected.Row;
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        var updatableColumns = row.Table.Columns
            .Cast<DataColumn>()
            .Where(c => !string.Equals(c.ColumnName, "__rowid__", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(c.ColumnName, "__selected__", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sets = new List<string>();
        for (int i = 0; i < updatableColumns.Count; i++)
        {
            var c = updatableColumns[i];
            var p = $"@v{i}";
            sets.Add($"[{c.ColumnName}] = {p}");
            cmd.Parameters.AddWithValue(p, ToDbValue(row[c.ColumnName]?.ToString()));
        }

        if (_hasRowId)
        {
            var ridText = row["__rowid__"]?.ToString();
            if (!long.TryParse(ridText, out var rid))
            {
                MessageBox.Show("Invalid RowID.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            cmd.CommandText = $"UPDATE [{_currentTable}] SET {string.Join(", ", sets)} WHERE rowid = @rid;";
            cmd.Parameters.AddWithValue("@rid", rid);
        }
        else
        {
            var where = new List<string>();
            for (int i = 0; i < _primaryKeyColumns.Count; i++)
            {
                var pk = _primaryKeyColumns[i];
                var p = $"@pk{i}";
                where.Add($"[{pk}] = {p}");
                var original = row[pk, DataRowVersion.Original]?.ToString();
                cmd.Parameters.AddWithValue(p, ToDbValue(original));
            }
            cmd.CommandText = $"UPDATE [{_currentTable}] SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", where)};";
        }

        var affected = await cmd.ExecuteNonQueryAsync();
        MessageBox.Show($"Updated {affected} row(s).", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        await LoadDataAsync(_currentTable);
    }

    private async void DeleteSelectedRow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentTable))
        {
            MessageBox.Show("No table selected.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_hasRowId && _primaryKeyColumns.Count == 0)
        {
            MessageBox.Show("This table cannot be deleted from (no RowID or primary key).", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DataGridView.CommitEdit(DataGridEditingUnit.Cell, true);
        DataGridView.CommitEdit(DataGridEditingUnit.Row, true);

        var view = DataGridView.ItemsSource as DataView;
        var checkedRows = view?.Table?.Rows
            .Cast<DataRow>()
            .Where(r => r.Table.Columns.Contains("__selected__") && (r["__selected__"] is bool b && b))
            .ToList() ?? [];

        if (checkedRows.Count == 0)
        {
            if (DataGridView.SelectedItem is DataRowView selectedView)
                checkedRows.Add(selectedView.Row);
        }

        if (checkedRows.Count == 0)
        {
            MessageBox.Show("Check at least one row (or select a row).", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"Delete {checkedRows.Count} row(s)?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var txn = conn.BeginTransaction();
        var totalDeleted = 0;

        foreach (var row in checkedRows)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = txn;

            if (_hasRowId)
            {
                var ridText = row["__rowid__"]?.ToString();
                if (!long.TryParse(ridText, out var rid))
                    continue;

                cmd.CommandText = $"DELETE FROM [{_currentTable}] WHERE rowid = @rid;";
                cmd.Parameters.AddWithValue("@rid", rid);
            }
            else
            {
                var where = new List<string>();
                for (int i = 0; i < _primaryKeyColumns.Count; i++)
                {
                    var pk = _primaryKeyColumns[i];
                    var p = $"@pk{i}";
                    where.Add($"[{pk}] = {p}");
                    cmd.Parameters.AddWithValue(p, ToDbValue(row[pk]?.ToString()));
                }
                cmd.CommandText = $"DELETE FROM [{_currentTable}] WHERE {string.Join(" AND ", where)};";
            }

            totalDeleted += await cmd.ExecuteNonQueryAsync();
        }

        await txn.CommitAsync();

        MessageBox.Show($"Deleted {totalDeleted} row(s).", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        await LoadDataAsync(_currentTable);
    }

    private void SelectAllRows_Click(object sender, RoutedEventArgs e)
    {
        if (DataGridView.ItemsSource is not DataView view || view.Table is null || !view.Table.Columns.Contains("__selected__"))
            return;

        foreach (DataRow row in view.Table.Rows)
            row["__selected__"] = true;

        DataGridView.Items.Refresh();
    }

    private void ClearSelectedRows_Click(object sender, RoutedEventArgs e)
    {
        if (DataGridView.ItemsSource is not DataView view || view.Table is null || !view.Table.Columns.Contains("__selected__"))
            return;

        foreach (DataRow row in view.Table.Rows)
            row["__selected__"] = false;

        DataGridView.Items.Refresh();
    }

    private async void DeleteAllRows_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentTable))
        {
            MessageBox.Show("No table selected.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete all rows from '{_currentTable}'? This cannot be undone.",
            "Confirm Delete All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM [{_currentTable}];";

        var affected = await cmd.ExecuteNonQueryAsync();

        MessageBox.Show($"Deleted {affected} row(s) from '{_currentTable}'.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        await LoadDataAsync(_currentTable);
    }

    private void RefreshBackups_Click(object sender, RoutedEventArgs e)
    {
        LoadBackups();
    }

    private void OpenSelectedBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupFileItem backup)
        {
            MessageBox.Show("Select a backup first.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(backup.FullPath))
        {
            MessageBox.Show("Backup file not found.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadBackups();
            return;
        }

        var win = new DatabaseExplorerWindow(backup.FullPath, $"Backup - {backup.FileName}")
        {
            Owner = this
        };
        win.Show();
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = GetBackupDirectories().FirstOrDefault(Directory.Exists);
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show("No backup folder found.", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open folder:\n{ex.Message}", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadBackups()
    {
        var backups = new List<BackupFileItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in GetBackupDirectories().Where(Directory.Exists))
        {
            var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                if (!seen.Add(file))
                    continue;

                var info = new FileInfo(file);
                backups.Add(new BackupFileItem
                {
                    FileName = info.Name,
                    LastModified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    Size = FormatFileSize(info.Length),
                    FullPath = info.FullName
                });
            }
        }

        backups = backups
            .OrderByDescending(b => b.LastModified)
            .ThenBy(b => b.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        BackupsGrid.ItemsSource = backups;
        BackupsHintText.Text = backups.Count > 0
            ? $"Found {backups.Count} backup file(s)."
            : "No backups found. Checked current DB folder and common backup subfolders.";
    }

    private IEnumerable<string> GetBackupDirectories()
    {
        var dbDir = Path.GetDirectoryName(_dbPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dbDir))
            yield return dbDir;

        if (!string.IsNullOrWhiteSpace(dbDir))
            yield return Path.Combine(dbDir, "Backups");

        if (!string.IsNullOrWhiteSpace(dbDir))
            yield return Path.Combine(dbDir, "Backup");

        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STI_Attendance");
        yield return Path.Combine(appRoot, "Backups");
        yield return Path.Combine(appRoot, "Database", "Backups");
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d):N1} MB";
        return $"{bytes / (1024d * 1024d * 1024d):N1} GB";
    }

    private static object ToDbValue(string? text)
    {
        if (text == null) return DBNull.Value;
        return string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase) ? DBNull.Value : text;
    }

    private static string FormatForGrid(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return "NULL";

        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => FormatBlob(bytes),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string FormatBlob(byte[] bytes)
    {
        if (bytes.Length == 0) return "BLOB[0]";

        var previewLen = Math.Min(bytes.Length, 16);
        var preview = BitConverter.ToString(bytes, 0, previewLen).Replace("-", "");
        var suffix = bytes.Length > previewLen ? "…" : string.Empty;
        return $"BLOB[{bytes.Length}] 0x{preview}{suffix}";
    }

    private async void ExecuteSql_Click(object sender, RoutedEventArgs e)
    {
        var sql = SqlQueryTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
        {
            SqlStatusText.Text = "Please enter a SQL query/script.";
            return;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (IsResultQuery(sql))
            {
                using var reader = await cmd.ExecuteReaderAsync();
                var dt = new DataTable();

                for (int i = 0; i < reader.FieldCount; i++)
                    dt.Columns.Add(reader.GetName(i), typeof(string));

                while (await reader.ReadAsync())
                {
                    var row = dt.NewRow();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[i] = FormatForGrid(reader, i);
                    dt.Rows.Add(row);
                }

                SqlResultsGrid.ItemsSource = dt.DefaultView;
                SqlStatusText.Text = $"Query executed. {dt.Rows.Count} row(s) returned.";
            }
            else
            {
                var affected = await cmd.ExecuteNonQueryAsync();
                SqlResultsGrid.ItemsSource = null;
                SqlStatusText.Text = $"Command executed. {affected} row(s) affected.";

                await LoadTablesAsync();
                if (!string.IsNullOrWhiteSpace(_currentTable))
                {
                    await LoadSchemaAsync(_currentTable);
                    await LoadDataAsync(_currentTable);
                }
            }
        }
        catch (Exception ex)
        {
            SqlStatusText.Text = $"SQL error: {ex.Message}";
            MessageBox.Show($"SQL execution failed:\n{ex.Message}", "Database Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearSql_Click(object sender, RoutedEventArgs e)
    {
        SqlQueryTextBox.Clear();
        SqlResultsGrid.ItemsSource = null;
        SqlStatusText.Text = "Ready.";
        SqlQueryTextBox.Focus();
    }

    private static bool IsResultQuery(string sql)
    {
        var normalized = sql.TrimStart();
        return normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase);
    }
}
