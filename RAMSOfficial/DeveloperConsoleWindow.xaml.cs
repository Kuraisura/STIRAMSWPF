using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RAMSOfficial;

public partial class DeveloperConsoleWindow : Window
{
    private const string Prompt = "E:\\RAMSOfficial> ";
    private int _currentInputStart;

    public DeveloperConsoleWindow()
    {
        InitializeComponent();
        Closed += (_, _) => RestoreMainWindowFocus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WriteRaw("Microsoft Windows [Version 10.0]\r\n");
        WriteRaw("(c) Microsoft Corporation. All rights reserved.\r\n\r\n");
        WriteRaw("RAMS Developer Console\r\n");
        WriteRaw("Type 'help' for commands.\r\n\r\n");
        WritePrompt();
        TerminalTextBox.Focus();
    }

    private async Task ExecuteCommandAsync(string raw)
    {
        var input = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            WritePrompt();
            return;
        }

        var normalized = input.ToLowerInvariant();

        if (normalized is "help" or "?")
        {
            WriteLine("Commands:");
            WriteLine("  help");
            WriteLine("  list databases");
            WriteLine("  open offline db");
            WriteLine("  open stirams db");
            WriteLine("  open database <offline|stirams>");
            WriteLine("  show tables <offline|stirams>");
            WriteLine("  pending");
            WriteLine("  status");
            WriteLine("  sync now");
            WriteLine("  rebuild offline schema");
            WriteLine("  clear");
            WriteLine("  exit");
        }
        else if (normalized == "clear")
        {
            TerminalTextBox.Clear();
        }
        else if (normalized is "exit" or "close")
        {
            Close();
            return;
        }
        else if (normalized == "list databases")
        {
            ListDatabases();
        }
        else if (normalized is "open offline db" or "open offline database")
        {
            OpenDatabaseBrowser(App.HybridDatabase?.GetDatabasePath(), "rams_offline.db");
        }
        else if (normalized is "open stirams db" or "open stirams database")
        {
            OpenDatabaseBrowser(App.AttendanceSyncService?.GetDatabasePath(), "STIRAMS.db");
        }
        else if (normalized.StartsWith("open database "))
        {
            var token = normalized.Replace("open database ", "").Trim();
            if (token is "offline" or "rams" or "rams_offline")
                OpenDatabaseBrowser(App.HybridDatabase?.GetDatabasePath(), "rams_offline.db");
            else if (token is "stirams" or "local")
                OpenDatabaseBrowser(App.AttendanceSyncService?.GetDatabasePath(), "STIRAMS.db");
            else
                WriteLine("Unknown database token. Use: offline or stirams");
        }
        else if (normalized.StartsWith("show tables "))
        {
            var token = normalized.Replace("show tables ", "").Trim();
            if (token is "offline" or "rams" or "rams_offline")
                OpenDatabaseBrowser(App.HybridDatabase?.GetDatabasePath(), "rams_offline.db");
            else if (token is "stirams" or "local")
                OpenDatabaseBrowser(App.AttendanceSyncService?.GetDatabasePath(), "STIRAMS.db");
            else
                WriteLine("Unknown database token. Use: offline or stirams");
        }
        else if (normalized == "pending")
        {
            var hybridPending = App.HybridDatabase != null ? await App.HybridDatabase.GetPendingCountAsync() : 0;
            var sovereignPending = App.AttendanceSyncService != null ? await App.AttendanceSyncService.GetPendingCountAsync() : 0;
            WriteLine($"Pending (Hybrid): {hybridPending}");
            WriteLine($"Pending (STIRAMS): {sovereignPending}");
            WriteLine($"Total Pending: {hybridPending + sovereignPending}");
        }
        else if (normalized == "status")
        {
            var state = App.HybridDatabase?.NetStatus.CurrentState.ToString() ?? "Unknown";
            var hasOverride = TimeChangerWindow.IsTimeOverridden();
            WriteLine($"Network: {state}");
            WriteLine($"Time Override: {(hasOverride ? "ACTIVE" : "OFF")}");
            if (App.AttendanceSyncService?.LastSyncUtc != null)
                WriteLine($"Last Sync: {App.AttendanceSyncService.LastSyncUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            else
                WriteLine("Last Sync: (none)");
        }
        else if (normalized is "sync now" or "force sync")
        {
            try
            {
                WriteLine("Triggering immediate sync...");

                if (App.HybridDatabase != null)
                {
                    await App.HybridDatabase.RefreshEmployeesAndSchedulesAsync();
                    await App.HybridDatabase.ForceUploadPendingTodatabaseAsync();
                }

                if (App.AttendanceSyncService != null)
                    await App.AttendanceSyncService.ForceSyncAsync();

                WriteLine("✓ Sync completed (or queued if currently offline).");
            }
            catch (Exception ex)
            {
                WriteLine($"✗ Sync failed: {ex.Message}");
            }
        }
        else if (normalized is "rebuild offline schema" or "rebuild offline db" or "clone database schema")
        {
            try
            {
                WriteLine("Replacing rams_offline.db using STIRAMS.db snapshot...");
                var localDb = new Services.LocalDatabaseService();
                var stiramsPath = App.AttendanceSyncService?.GetDatabasePath();
                if (string.IsNullOrWhiteSpace(stiramsPath))
                    throw new InvalidOperationException("STIRAMS.db path is unavailable.");

                await localDb.ReplaceWithDatabaseAsync(stiramsPath);
                WriteLine("✓ Offline database replaced from STIRAMS.db.");
                WriteLine($"Path: {localDb.GetDatabasePath()}");

                if (App.HybridDatabase != null)
                {
                    await App.HybridDatabase.RefreshEmployeesAndSchedulesAsync();
                    await App.HybridDatabase.ForceUploadPendingTodatabaseAsync();
                    WriteLine("✓ Master data refresh + pending upload triggered.");
                }

                if (App.AttendanceSyncService != null)
                    await App.AttendanceSyncService.ForceSyncAsync();
            }
            catch (Exception ex)
            {
                WriteLine($"✗ Rebuild failed: {ex.Message}");
            }
        }
        else
        {
            WriteLine("Unknown command. Type 'help'.");
        }

        WritePrompt();
    }

    private void ListDatabases()
    {
        var offlinePath = App.HybridDatabase?.GetDatabasePath();
        var stiramsPath = App.AttendanceSyncService?.GetDatabasePath();

        WriteLine("Databases:");
        WriteDbInfo("rams_offline.db", offlinePath);
        WriteDbInfo("STIRAMS.db", stiramsPath);
    }

    private void WriteDbInfo(string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WriteLine($"  {name}: (path unavailable)");
            return;
        }

        if (!File.Exists(path))
        {
            WriteLine($"  {name}: NOT FOUND");
            WriteLine($"    Path: {path}");
            return;
        }

        var fi = new FileInfo(path);
        WriteLine($"  {name}: OK ({fi.Length / 1024.0:N1} KB)");
        WriteLine($"    Path: {path}");
    }

    private void OpenDatabaseBrowser(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WriteLine($"{label}: path unavailable.");
            return;
        }

        if (!File.Exists(path))
        {
            WriteLine($"{label}: file not found.");
            WriteLine(path);
            return;
        }

        try
        {
            var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
            Window ownerWindow = (Window?)mainWindow ?? this;
            var dbWindow = new DatabaseExplorerWindow(path, label)
            {
                Owner = ownerWindow
            };
            dbWindow.Closed += (_, _) => RestoreMainWindowFocus();
            dbWindow.Show();
            WriteLine($"Opened {label} in internal database explorer.");
        }
        catch (Exception ex)
        {
            WriteLine($"Failed to open {label}: {ex.Message}");
        }
    }

    private void WritePrompt()
    {
        WriteRaw(Prompt);
        _currentInputStart = TerminalTextBox.Text.Length;
        TerminalTextBox.CaretIndex = TerminalTextBox.Text.Length;
        TerminalTextBox.ScrollToEnd();
    }

    private void WriteLine(string text)
    {
        WriteRaw(text + Environment.NewLine);
    }

    private void WriteRaw(string text)
    {
        TerminalTextBox.AppendText(text);
        TerminalTextBox.CaretIndex = TerminalTextBox.Text.Length;
        TerminalTextBox.ScrollToEnd();
    }

    private async void TerminalTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var command = TerminalTextBox.Text.Length >= _currentInputStart
                ? TerminalTextBox.Text[_currentInputStart..]
                : string.Empty;

            WriteRaw(Environment.NewLine);
            await ExecuteCommandAsync(command);
            return;
        }

        // Keep caret/editing only on current input segment
        if (e.Key == Key.Back && TerminalTextBox.CaretIndex <= _currentInputStart)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && TerminalTextBox.CaretIndex <= _currentInputStart)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Home)
        {
            e.Handled = true;
            TerminalTextBox.CaretIndex = _currentInputStart;
            return;
        }

        if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.PageUp)
        {
            e.Handled = true;
            return;
        }

        if (TerminalTextBox.CaretIndex < _currentInputStart)
            TerminalTextBox.CaretIndex = TerminalTextBox.Text.Length;
    }

    private void TerminalTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // behave like classic cmd: always type at current prompt tail
        e.Handled = true;
        TerminalTextBox.Focus();
        TerminalTextBox.CaretIndex = TerminalTextBox.Text.Length;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void RestoreMainWindowFocus()
    {
        try
        {
            var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
            if (mainWindow == null)
                return;

            mainWindow.Activate();
            mainWindow.Focus();
        }
        catch
        {
        }
    }
}
