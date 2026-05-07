using System.Windows;
using RAMSOfficial.Services;

namespace RAMSOfficial;

/// <summary>
/// Add this to test database connectivity
/// </summary>
public class DatabaseDiagnostics
{
    public static async Task<string> RunDiagnosticsAsync()
    {
        var report = "=== DATABASE DIAGNOSTICS REPORT ===\n\n";
        
        try
        {
            var dbService = new DatabaseService();
            var schemaService = new DatabaseSchemaService(
                dbService.GetConnectionString()
            );

            // Test 1: Connection
            report += "TEST 1: Database Connection\n";
            try
            {
                var tables = await schemaService.GetAllTablesAsync();
                report += $"? SUCCESS - Found {tables.Count} tables\n\n";
            }
            catch (Exception ex)
            {
                report += $"? FAILED - {ex.Message}\n\n";
                return report;
            }

            // Test 2: Employees Table
            report += "TEST 2: Employees Table Structure\n";
            try
            {
                var empColumns = await schemaService.GetTableColumnsAsync("employees");
                report += $"? Found {empColumns.Count} columns:\n";
                foreach (var col in empColumns.Take(10))
                {
                    report += $"  - {col.ColumnName} ({col.DataType})\n";
                }
                if (empColumns.Count > 10)
                    report += $"  ... and {empColumns.Count - 10} more\n";
                report += "\n";
            }
            catch (Exception ex)
            {
                report += $"? FAILED - {ex.Message}\n\n";
            }

            // Test 3: Attendance Logs Table
            report += "TEST 3: Attendance Logs Table Structure\n";
            try
            {
                var logColumns = await schemaService.GetTableColumnsAsync("attendance_logs");
                report += $"? Found {logColumns.Count} columns:\n";
                foreach (var col in logColumns.Take(10))
                {
                    report += $"  - {col.ColumnName} ({col.DataType})\n";
                }
                if (logColumns.Count > 10)
                    report += $"  ... and {logColumns.Count - 10} more\n";
                report += "\n";
            }
            catch (Exception ex)
            {
                report += $"? FAILED - {ex.Message}\n\n";
            }

            // Test 4: RFID Column Detection
            report += "TEST 4: RFID Column Detection\n";
            try
            {
                var empColumns = await schemaService.GetTableColumnsAsync("employees");
                var rfidColumns = empColumns.Where(c => 
                    c.ColumnName.ToLower().Contains("rfid") ||
                    c.ColumnName.ToLower().Contains("card") ||
                    c.ColumnName.ToLower().Contains("badge")
                ).ToList();

                if (rfidColumns.Any())
                {
                    report += $"? Found RFID-related columns:\n";
                    foreach (var col in rfidColumns)
                    {
                        report += $"  - {col.ColumnName}\n";
                    }
                }
                else
                {
                    report += "?? No obvious RFID column found\n";
                    report += "   Possible columns to use:\n";
                    var possibleColumns = empColumns.Where(c =>
                        c.ColumnName.ToLower().Contains("employee") ||
                        c.ColumnName.ToLower().Contains("number") ||
                        c.ColumnName.ToLower().Contains("code")
                    ).ToList();
                    foreach (var col in possibleColumns)
                    {
                        report += $"  - {col.ColumnName}\n";
                    }
                }
                report += "\n";
            }
            catch (Exception ex)
            {
                report += $"? FAILED - {ex.Message}\n\n";
            }

            // Test 5: Sample Employee Query
            report += "TEST 5: Sample Employee Query\n";
            try
            {
                var employee = await dbService.GetEmployeeByRfidAsync("TEST_12345");
                if (employee != null)
                {
                    report += $"? Query executed (found employee)\n";
                }
                else
                {
                    report += $"? Query executed (no employee found with TEST_12345)\n";
                }
                report += "\n";
            }
            catch (Exception ex)
            {
                report += $"? FAILED - {ex.Message}\n\n";
            }

            // Test 6: Table Row Counts
            report += "TEST 6: Data Check\n";
            try
            {
                var info = await schemaService.GetDatabaseInfoAsync();
                if (info.ContainsKey("RowCounts"))
                {
                    var rowCounts = (List<TableRowCount>)info["RowCounts"];
                    var empCount = rowCounts.FirstOrDefault(r => r.TableName == "employees");
                    var logCount = rowCounts.FirstOrDefault(r => r.TableName == "attendance_logs");

                    report += $"  Employees: {empCount?.RowCount ?? 0} rows\n";
                    report += $"  Attendance Logs: {logCount?.RowCount ?? 0} rows\n";
                }
                report += "\n";
            }
            catch (Exception ex)
            {
                report += $"?? Could not get row counts: {ex.Message}\n\n";
            }

            report += "=== DIAGNOSTICS COMPLETE ===\n";
        }
        catch (Exception ex)
        {
            report += $"\n? CRITICAL ERROR: {ex.Message}\n";
            report += $"\nStack Trace:\n{ex.StackTrace}\n";
        }

        return report;
    }
}
