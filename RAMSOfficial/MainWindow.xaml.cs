using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RAMSOfficial.ViewModels;
using System.Windows.Interop;
using System.Diagnostics;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RAMSOfficial.Services;
using RAMSOfficial.Helpers;
using Dapper;

namespace RAMSOfficial;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer? _focusTimer;
    private readonly DispatcherTimer? _rfidFocusGuardTimer;
    private readonly DispatcherTimer _reportingDayCountdownTimer;
    private readonly DispatcherTimer _mainWindowRefreshTimer;
    private DeveloperConsoleWindow? _developerConsoleWindow;
    private const string DeveloperConsolePassword = "121121212112@Aa";
    private bool _isFullscreen = true;
    private int _reportingDayCountdown = 3;
    private bool _reportingDayDialogShown = false; // Prevent multiple dialogs
    private int _warningDialogCountdown = 3; // Countdown for warning/information dialog
    private readonly DispatcherTimer _warningDialogCountdownTimer; // Timer for warning dialog
    private int _onlineClassCountdown = 3; // Countdown for online class dialog
    private readonly DispatcherTimer _onlineClassCountdownTimer; // Timer for online class dialog

    // RFID buffer to accumulate digits — uses Stopwatch for sub-ms precision
    private readonly StringBuilder _rfidBuffer = new StringBuilder();
    private readonly Stopwatch _rfidStopwatch = new Stopwatch();
    private const int RFID_TIMEOUT_MS = 300; // Reduced from 500ms — RFID readers send full UID within ~100ms

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Setup timers
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();

        // Refresh MainWindow data every 30 seconds so remote changes
        // (e.g., deleted attendance_logs in database) are reflected in UI.
        _mainWindowRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _mainWindowRefreshTimer.Tick += MainWindowRefreshTimer_Tick;
        _mainWindowRefreshTimer.Start();

        // CRITICAL: Focus guard timer - ensures RFID TextBox ALWAYS has focus
        // Checks every 250ms and restores focus if lost
        _rfidFocusGuardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _rfidFocusGuardTimer.Tick += RfidFocusGuardTimer_Tick;
        _rfidFocusGuardTimer.Start();
        
        // Legacy focus timer (kept for compatibility)
        _focusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _focusTimer.Tick += FocusTimer_Tick;
        _focusTimer.Start();

        // Reporting Day Countdown Timer (for auto-close)
        _reportingDayCountdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _reportingDayCountdownTimer.Tick += ReportingDayCountdownTimer_Tick;
        
        // Warning Dialog Countdown Timer (for 3-second auto-close)
        _warningDialogCountdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _warningDialogCountdownTimer.Tick += WarningDialogCountdownTimer_Tick;
        
        // Online Class Dialog Countdown Timer (for 3-second auto-close)
        _onlineClassCountdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _onlineClassCountdownTimer.Tick += OnlineClassCountdownTimer_Tick;
        
        UpdateClock();

        Debug.WriteLine("----------------------------------------");
        Debug.WriteLine("  RFID FOCUS GUARD ENABLED");
        Debug.WriteLine("  - Focus check every 250ms");
        Debug.WriteLine("  - Auto-restore if focus lost");
        Debug.WriteLine("  - Works even after dialogs close");
        Debug.WriteLine("  ");
        Debug.WriteLine("  ? REPORTING DAY DIALOG READY");
        Debug.WriteLine("  - Shows when employee taps on reporting day");
        Debug.WriteLine("  - Auto-closes after 3 second countdown");
        Debug.WriteLine("----------------------------------------");
    }
    

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        // Set window to cover full screen (windowed fullscreen) using WPF SystemParameters
        this.Left = 0;
        this.Top = 0;
        this.Width = SystemParameters.PrimaryScreenWidth;
        this.Height = SystemParameters.PrimaryScreenHeight;
        
        Debug.WriteLine($"\uE946 Windowed fullscreen: {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");  // Info icon
    }

    private void EnterFullscreen()
    {
        this.WindowStyle = WindowStyle.None;
        this.WindowState = WindowState.Maximized;
        this.ResizeMode = ResizeMode.NoResize;
        this.Topmost = false;
        this.ShowInTaskbar = true;
        _isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        // Windowed mode (default)
        this.WindowStyle = WindowStyle.SingleBorderWindow;
        this.WindowState = WindowState.Normal;
        this.ResizeMode = ResizeMode.CanResize;
        this.Topmost = false;
        this.ShowInTaskbar = true;
        _isFullscreen = false;
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // -------------------------------------------
        // PRIORITY 1: TIME CHANGER - Press 'T' key (HIGHEST PRIORITY - Must check FIRST!)
        // -------------------------------------------
        if (e.Key == Key.T && !_viewModel.IsProcessing)
        {
            e.Handled = true;
            Debug.WriteLine("\uE121 T key pressed - Opening Time Changer Window");  // Clock icon
            await OpenTimeChangerAsync();
            return;
        }

        // Developer Console - Press `~` (Oem3)
        if (e.Key == Key.Oem3)
        {
            e.Handled = true;
            OpenDeveloperConsole();
            return;
        }
        
        // -------------------------------------------
        // FORCE DATABASE POPULATION - Press 'P' key
        // -------------------------------------------
        if (e.Key == Key.P && !_viewModel.IsProcessing)
        {
            e.Handled = true;
            Debug.WriteLine("?? P key pressed - Forcing database population");
            await ForcePopulateLocalDatabaseAsync();
            return;
        }
        
        // -------------------------------------------
        // SHOW LOCAL DATABASE CONTENTS - Press 'D' key (Debug)
        // -------------------------------------------
        if (e.Key == Key.D && !_viewModel.IsProcessing)
        {
            e.Handled = true;
            Debug.WriteLine("?? D key pressed - Showing local database contents");
            await ShowLocalDatabaseContentsAsync();
            return;
        }

        // -------------------------------------------
        // MANUAL REFRESH - Press 'R' key
        // -------------------------------------------
        if (e.Key == Key.R && !_viewModel.IsProcessing)
        {
            e.Handled = true;
            await RefreshMainScreenAsync("ManualKey:R");
            return;
        }

        // -------------------------------------------
        // KIOSK TAP-IN SCREEN - Press F5 key
        // Opens the dedicated full-screen TapIn kiosk window.
        // -------------------------------------------
        if (e.Key == Key.F5 && !_viewModel.IsProcessing)
        {
            e.Handled = true;
            Debug.WriteLine("?? F5 pressed — launching Kiosk Tap-In screen");
            OpenTapInKiosk();
            return;
        }

        // -------------------------------------------
        // PRIORITY 2: SPACEBAR for manual entry
        // -------------------------------------------
        if (e.Key == Key.Space && _viewModel.IsWaitingForTap)
        {
            e.Handled = true;
            await ShowManualIdInputAsync();
            return;
        }

        // -------------------------------------------
        // F11 - No longer needed in fullscreen mode
        // -------------------------------------------
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            Debug.WriteLine("\uF140 F11 blocked - already in windowed fullscreen mode");  // Block icon
            return;
        }

        // -------------------------------------------
        // ESC key allowed (no blocking)
        // -------------------------------------------
        if (e.Key == Key.Escape)
        {
            // Don't handle - let it pass through
            return;
        }

        // -------------------------------------------
        // Handle RFID input - accumulate digits and letters (EXCEPT 'T')
        // -------------------------------------------
        if (_viewModel.IsWaitingForTap)
        {
            // Mark as handled to prevent further processing
            e.Handled = true;
            ProcessRfidKey(e.Key);
        }
    }

    private void OpenDeveloperConsole()
    {
        var auth = new DeveloperConsoleAuthWindow(DeveloperConsolePassword)
        {
            Owner = this
        };

        var ok = auth.ShowDialog();
        if (ok != true || !auth.IsAuthenticated)
        {
            return;
        }

        if (_developerConsoleWindow != null && _developerConsoleWindow.IsLoaded)
        {
            _developerConsoleWindow.Activate();
            return;
        }

        _developerConsoleWindow = new DeveloperConsoleWindow
        {
            Owner = this
        };

        _developerConsoleWindow.Closed += (_, _) => _developerConsoleWindow = null;
        _developerConsoleWindow.Show();
    }

    private async Task OpenTimeChangerAsync()
    {
        try
        {
            Debug.WriteLine("");
            Debug.WriteLine("---------------------------------------------");
            Debug.WriteLine("¦  ? OPENING TIME CHANGER WINDOW                      ¦");  // Clock icon
            Debug.WriteLine("---------------------------------------------");
            
            var timeChanger = new TimeChangerWindow();
            var result = timeChanger.ShowDialog();
            
            if (result == true)
            {
                // Time was changed - update UI
                UpdateClockDisplay();
                
                Debug.WriteLine("? Time Changer closed - time override applied");;  // CheckMark
            }
            else
            {
                Debug.WriteLine("? Time Changer closed - no changes made");  // Cancel
            }
            
            // Ensure RFID input has focus after dialog closes
            await Task.Delay(100);
            EnsureRfidFocus("TimeChangerClosed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error opening Time Changer: {ex.Message}");  // Error icon
            MessageBox.Show(
                $"Error opening Time Changer:\n\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
    
    private async Task ForcePopulateLocalDatabaseAsync()
    {
        try
        {
            Debug.WriteLine("");
            Debug.WriteLine("---------------------------------------------");
            Debug.WriteLine("¦  ?? FORCING DATABASE POPULATION                      ¦");
            Debug.WriteLine("---------------------------------------------");
            
            var result = MessageBox.Show(
                "This will download ALL employee data from database and overwrite your local cache.\n\n" +
                "This requires an active internet connection.\n\n" +
                "Do you want to continue?",
                "Force Database Population",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                Debug.WriteLine("? User confirmed - starting population...");
                
                await Tools.PopulateLocalDatabase.PopulateAsync();
                
                MessageBox.Show(
                    "Database population completed!\n\n" +
                    "Check the Output window (View ? Output) for details.",
                    "Population Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                
                Debug.WriteLine("? Population completed successfully");
            }
            else
            {
                Debug.WriteLine("? User cancelled population");
            }
            
            // Ensure RFID input has focus
            await Task.Delay(100);
            EnsureRfidFocus("PopulationComplete");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error during population: {ex.Message}");
            MessageBox.Show(
                $"Error during database population:\n\n{ex.Message}\n\n" +
                "Make sure you have an active internet connection and try again.",
                "Population Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
    
    private async Task ShowLocalDatabaseContentsAsync()
    {
        try
        {
            Debug.WriteLine("");
            Debug.WriteLine("---------------------------------------------");
            Debug.WriteLine("¦  ?? LOCAL DATABASE CONTENTS                         ¦");
            Debug.WriteLine("---------------------------------------------");

            var stats = await App.HybridDatabase.GetDatabaseStatsAsync();

            var ramsPath  = App.HybridDatabase.GetDatabasePath();
            var stiraPath = App.AttendanceSyncService?.GetDatabasePath() ?? "N/A";

            Debug.WriteLine($"");
            Debug.WriteLine($"?? DATABASE PATHS (open these in DBeaver):");
            Debug.WriteLine($"   rams_offline.db : {ramsPath}");
            Debug.WriteLine($"   STIRAMS.db      : {stiraPath}");
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Database Statistics:");
            Debug.WriteLine($"   Employees: {stats.EmployeeCount}");
            Debug.WriteLine($"   Attendance Logs: {stats.AttendanceLogCount}");
            Debug.WriteLine($"   Pending Sync: {stats.PendingCount}");
            Debug.WriteLine($"   Database Size: {stats.GetDatabaseSizeFormatted()}");
            Debug.WriteLine($"   Last Sync: {stats.LastSyncTime ?? "Never"}");

            // Query local database to show employee RFID codes
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={ramsPath}");
            await connection.OpenAsync();

            var employees = await Dapper.SqlMapper.QueryAsync<dynamic>(connection, @"
                SELECT employee_id, rfid_code, full_name, department, employment_status
                FROM employees
                WHERE is_active = 1
                ORDER BY full_name
            ");

            Debug.WriteLine($"");
            Debug.WriteLine($"?? Cached Employees ({employees.Count()}):");
            Debug.WriteLine($"+----------------------------------------------------------------------------+");
            Debug.WriteLine($"¦ ID ¦ RFID Code    ¦ Name                          ¦ Department             ¦");
            Debug.WriteLine($"¦----+--------------+-------------------------------+------------------------¦");

            foreach (var emp in employees)
            {
                Debug.WriteLine($"¦ {emp.employee_id,2} ¦ {emp.rfid_code,-12} ¦ {emp.full_name,-29} ¦ {emp.department,-22} ¦");
            }

            Debug.WriteLine($"+----------------------------------------------------------------------------+");

            MessageBox.Show(
                $"DATABASE PATHS — open in DBeaver:\n\n" +
                $"rams_offline.db:\n{ramsPath}\n\n" +
                $"STIRAMS.db (database Replica):\n{stiraPath}\n\n" +
                $"-----------------------------\n" +
                $"Employees: {stats.EmployeeCount}\n" +
                $"Attendance Logs: {stats.AttendanceLogCount}\n" +
                $"Pending Sync: {stats.PendingCount}\n" +
                $"Database Size: {stats.GetDatabaseSizeFormatted()}\n\n" +
                $"Check the Output window for full employee list.",
                "Local Database Info",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error showing database contents: {ex.Message}");
            MessageBox.Show(
                $"Error: {ex.Message}",
                "Database Query Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Opens the dedicated full-screen Kiosk Tap-In window.
    /// Hides the MainWindow while the kiosk is open and restores it when closed.
    /// Press F5 from the main screen to launch.
    /// </summary>
    private void OpenTapInKiosk()
    {
        try
        {
            Debug.WriteLine("");
            Debug.WriteLine("---------------------------------------------");
            Debug.WriteLine("¦  ?? OPENING KIOSK TAP-IN SCREEN          ¦");
            Debug.WriteLine("---------------------------------------------");

            // Pause focus guards while kiosk is active
            _rfidFocusGuardTimer?.Stop();
            _focusTimer?.Stop();

            this.Hide();

            var kioskWindow = new TapInWindow();
            kioskWindow.Closed += (_, _) =>
            {
                Debug.WriteLine("?? Kiosk Tap-In closed — restoring MainWindow");
                this.Show();
                this.Activate();

                // Resume focus guards
                _rfidFocusGuardTimer?.Start();
                _focusTimer?.Start();
                EnsureRfidFocus("TapInKioskClosed");
            };
            kioskWindow.Show();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error opening Kiosk Tap-In: {ex.Message}");
            this.Show();
            _rfidFocusGuardTimer?.Start();
            _focusTimer?.Start();
        }
    }

    private void ProcessRfidKey(Key key)
    {
        // Check for buffer timeout using high-resolution Stopwatch
        if (_rfidStopwatch.IsRunning && _rfidStopwatch.ElapsedMilliseconds > RFID_TIMEOUT_MS && _rfidBuffer.Length > 0)
        {
            Debug.WriteLine($"\u26A0\uFE0F Buffer timeout ({_rfidStopwatch.ElapsedMilliseconds}ms) - clearing buffer");
            _rfidBuffer.Clear();
        }

        _rfidStopwatch.Restart();

        // Handle ENTER key - process complete RFID scan
        if (key == Key.Enter || key == Key.Return)
        {
            _rfidStopwatch.Stop();
            var rfidCode = _rfidBuffer.ToString().Trim();
            Debug.WriteLine($"\u2139\uFE0F Raw buffer before ENTER: '{rfidCode}' (Length: {rfidCode.Length})");

            _rfidBuffer.Clear();

            if (!string.IsNullOrWhiteSpace(rfidCode))
            {
                Debug.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
                Debug.WriteLine($"\uD83D\uDCB3 RFID CARD TAPPED!");
                Debug.WriteLine($"   Code: {rfidCode}");
                Debug.WriteLine($"   Length: {rfidCode.Length}");
                Debug.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");

                // Set the RFID input and process
                _viewModel.RfidInput = rfidCode;

                if (_viewModel.ProcessRfidCommand.CanExecute(null))
                {
                    _viewModel.ProcessRfidCommand.Execute(null);
                }
            }
            else
            {
                Debug.WriteLine("\u26A0\uFE0F ENTER pressed but buffer is empty!");
            }
            return;
        }

        // Accumulate numeric digits (RFID codes are usually numbers)
        var digit = GetDigitFromKey(key);
        if (digit.HasValue)
        {
            _rfidBuffer.Append(digit.Value);
            return;
        }

        // Also handle letter keys for alphanumeric RFID codes
        var letter = GetLetterFromKey(key);
        if (letter.HasValue)
        {
            _rfidBuffer.Append(letter.Value);
        }
    }

    private char? GetDigitFromKey(Key key)
    {
        // Number keys (top row)
        if (key >= Key.D0 && key <= Key.D9)
        {
            return (char)('0' + (key - Key.D0));
        }

        // Numpad keys
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return (char)('0' + (key - Key.NumPad0));
        }

        return null;
    }

    private char? GetLetterFromKey(Key key)
    {
        // Letter keys (A-Z)
        if (key >= Key.A && key <= Key.Z)
        {
            return (char)('A' + (key - Key.A));
        }

        return null;
    }

    private async Task ShowManualIdInputAsync()
    {
        try
        {
            var dialog = new ManualIdInputDialog();
            var result = dialog.ShowDialog();
            
            if (result == true && dialog.WasSubmitted && !string.IsNullOrEmpty(dialog.EnteredId))
            {
                Debug.WriteLine($"?? Manual ID entered: {dialog.EnteredId}");
                
                // Set the RFID input and process it
                _viewModel.RfidInput = dialog.EnteredId;
                
                // Process the input
                if (_viewModel.ProcessRfidCommand.CanExecute(null))
                {
                    _viewModel.ProcessRfidCommand.Execute(null);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error showing manual input dialog: {ex.Message}");
            MessageBox.Show(
                $"Error: {ex.Message}",
                "Manual Input Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateClock();
    }

    private void ReportingDayCountdownTimer_Tick(object? sender, EventArgs e)
    {
        _reportingDayCountdown--;
        UpdateCountdownButtonText();

        if (_reportingDayCountdown <= 0)
        {
            _reportingDayCountdownTimer.Stop();
            CloseReportingDayDialog();
        }
    }

    private void UpdateCountdownButtonText()
    {
        if (_reportingDayCountdown > 0)
        {
            // Show countdown
            ReportingDayOkButton.Content = $"? OK, I UNDERSTAND ({_reportingDayCountdown}s)";
        }
        else
        {
            // Show normal button
            ReportingDayOkButton.Content = "? OK, I UNDERSTAND";
        }
    }

    public async Task ShowReportingDayInfoDialogAsync(string holidayName, string employeeFullName, string staffType, string employmentStatus)
    {
        // Prevent multiple dialogs
        if (_reportingDayDialogShown)
        {
            Debug.WriteLine($"?? Reporting day dialog already shown - skipping duplicate");
            return;
        }

        if (ReportingDayInfoOverlay.Visibility == Visibility.Visible)
        {
            Debug.WriteLine($"?? Reporting day dialog currently visible - skipping duplicate");
            return;
        }

        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"----------------------------------------");
            Debug.WriteLine($"?? SHOWING REPORTING DAY DIALOG AFTER TAP");
            Debug.WriteLine($"   Employee: {employeeFullName}");
            Debug.WriteLine($"   Staff Type: {staffType}");
            Debug.WriteLine($"   Employment Status: {employmentStatus}");
            Debug.WriteLine($"   Holiday: {holidayName}");
            Debug.WriteLine($"----------------------------------------");

            _reportingDayDialogShown = true;

            var currentTime = TimeChangerWindow.GetCurrentTime();
            
            // Update dialog content
            HolidayNameText.Text = holidayName;
            DateInfoText.Text = currentTime.ToString("dddd, MMMM dd, yyyy");
            TimeInfoText.Text = $"Current Time: {currentTime:hh:mm tt}";

            // Determine which staff instructions to show
            var isTeaching = staffType?.Equals("Teaching", StringComparison.OrdinalIgnoreCase) == true;
            var isNonTeaching = staffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
            var isPartTimeFull = employmentStatus?.Contains("Part", StringComparison.OrdinalIgnoreCase) == true && 
                                employmentStatus?.Contains("Full Load", StringComparison.OrdinalIgnoreCase) == true;
            var isRegular = employmentStatus?.Equals("Regular", StringComparison.OrdinalIgnoreCase) == true;

            // Hide all staff instruction sections first
            TeachingStaffInstructions.Visibility = Visibility.Collapsed;
            NonTeachingStaffInstructions.Visibility = Visibility.Collapsed;

            if (isTeaching && (isPartTimeFull || isRegular))
            {
                TeachingStaffInstructions.Visibility = Visibility.Visible;
                TeachingStatusText.Text = "Attendance Status: ADMIN TIME";
                Debug.WriteLine($"   ?? Showing TEACHING STAFF instructions (Admin Time)");
            }
            else if (isNonTeaching)
            {
                NonTeachingStaffInstructions.Visibility = Visibility.Visible;
                Debug.WriteLine($"   ?? Showing NON-TEACHING STAFF instructions (Normal calculation)");
            }
            else
            {
                NonTeachingStaffInstructions.Visibility = Visibility.Visible;
                Debug.WriteLine($"   ? Showing PART-TIME instructions (Normal calculation)");
            }

            // Show the overlay
            ReportingDayInfoOverlay.Visibility = Visibility.Visible;

            // Start 3-second countdown
            _reportingDayCountdown = 3;
            UpdateCountdownButtonText();
            _reportingDayCountdownTimer.Start();

            Debug.WriteLine($"? Reporting day dialog displayed");
            Debug.WriteLine($"?? Starting 3-second countdown for auto-close");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error showing reporting day dialog: {ex.Message}");
            _reportingDayDialogShown = false;
        }
    }

    private void ReportingDayOkButton_Click(object sender, RoutedEventArgs e)
    {
        _reportingDayCountdownTimer.Stop();
        CloseReportingDayDialog();
    }

    private void CloseReportingDayDialog()
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"--------------------");
            Debug.WriteLine($"? REPORTING DAY DIALOG CLOSED");
            Debug.WriteLine($"   Auto-closed after countdown or manual click");
            Debug.WriteLine($"--------------------");

            // Hide the overlay
            ReportingDayInfoOverlay.Visibility = Visibility.Collapsed;

            // Reset the flag after delay
            Task.Delay(5000).ContinueWith(_ => _reportingDayDialogShown = false);

            // Ensure RFID focus
            EnsureRfidFocus("ReportingDayDialogClosed");

            Debug.WriteLine($"? Ready for next employee tap");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error closing reporting day dialog: {ex.Message}");
        }
    }

    private void UpdateClock()
    {
        var currentTime = TimeChangerWindow.GetCurrentTime();

        if (TimeChangerWindow.IsTimeOverridden())
        {
            ClockTextBlock.Text = currentTime.ToString("hh:mm tt");
            DateTextBlockHeader.Text = currentTime.ToString("MMMM dd, yyyy");
            ClockTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD600")!);
            DateTextBlockHeader.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD600")!);
        }
        else
        {
            ClockTextBlock.Text = currentTime.ToString("hh:mm tt");
            DateTextBlockHeader.Text = currentTime.ToString("MMMM dd, yyyy");
            ClockTextBlock.Foreground = new SolidColorBrush(Colors.White);
            DateTextBlockHeader.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#90CAF9")!);
        }
    }

    private void UpdateClockDisplay()
    {
        UpdateClock();
    }

    private void RfidTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("? RFID TextBox has focus - ready for card tap");
    }

    private void RfidTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsWaitingForTap)
        {
            Debug.WriteLine("?? RFID TextBox lost focus - regaining focus!");
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                RfidTextBox.Focus();
                Keyboard.Focus(RfidTextBox);
            }));
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureRfidFocus("Window_Loaded");
    }

    private void Window_activated(object sender, EventArgs e)
    {
        EnsureRfidFocus("Window_activated");
    }

    private void Window_GotFocus(object sender, RoutedEventArgs e)
    {
        EnsureRfidFocus("Window_GotFocus");
    }

    private void EnsureRfidFocus(string caller)
    {
        if (_viewModel.IsWaitingForTap && !RfidTextBox.IsFocused)
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"?? ENSURING RFID FOCUS (called from: {caller})");
            
            RfidTextBox.Focus();
            Keyboard.Focus(RfidTextBox);
            
            this.Activate();
            this.Focus();
            
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    RfidTextBox.Focus();
                    Keyboard.Focus(RfidTextBox);
                })
            );
            
            Debug.WriteLine($"   IsKeyboardFocused: {RfidTextBox.IsKeyboardFocused}");
            Debug.WriteLine($"   IsFocused: {RfidTextBox.IsFocused}");
            Debug.WriteLine($"");
        }
    }

    private void FocusTimer_Tick(object? sender, EventArgs e)
    {
        if (_viewModel.IsWaitingForTap && !RfidTextBox.IsFocused)
        {
            Debug.WriteLine("?? Auto-focusing RFID TextBox...");
            RfidTextBox.Focus();
            Keyboard.Focus(RfidTextBox);
        }
    }

    private void RfidFocusGuardTimer_Tick(object? sender, EventArgs e)
    {
        if (_viewModel.IsWaitingForTap && !RfidTextBox.IsKeyboardFocused)
        {
            Debug.WriteLine($"?? RFID FOCUS LOST - AUTO-RESTORING...");
            EnsureRfidFocus("RfidFocusGuardTimer");
        }
    }

    private async void MainWindowRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshMainScreenAsync("AutoTimer:30s");
    }

    private async Task RefreshMainScreenAsync(string source)
    {
        try
        {
            Debug.WriteLine($"[MainWindow] Refresh triggered from {source}");

            // Refresh recent attendance data
            await _viewModel.RefreshRecentAttendanceAsync();

            // Refresh clock/date (supports custom time override mode)
            UpdateClockDisplay();

            // Keep RFID workflow ready after refresh
            EnsureRfidFocus(source);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] refresh failed ({source}): {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _mainWindowRefreshTimer.Stop();
        _focusTimer?.Stop();
        _rfidFocusGuardTimer?.Stop();
        _reportingDayCountdownTimer?.Stop();
        _warningDialogCountdownTimer?.Stop();
        _onlineClassCountdownTimer?.Stop();

        if (_developerConsoleWindow != null)
        {
            _developerConsoleWindow.Close();
            _developerConsoleWindow = null;
        }

        base.OnClosed(e);
    }
    
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Debug.WriteLine("?? Window closing via Alt+F4 or close request");
    }

    public async Task ShowReportingDayWarningAsync(string holidayName, DateTime currentDate)
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"----------------------------------------");
            Debug.WriteLine($"?? SHOWING REPORTING DAY WARNING");
            Debug.WriteLine($"   Holiday: {holidayName}");
            Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd}");
            Debug.WriteLine($"----------------------------------------");

            WarningHolidayText.Text = holidayName;
            WarningDateText.Text = $"Date: {currentDate:dddd, MMMM dd, yyyy}";
            WarningHolidayTypeText.Text = "Holiday: suspended_asynchronous";

            ReportingDayWarningOverlay.Visibility = Visibility.Visible;

            _warningDialogCountdown = 3;
            UpdateWarningDialogCountdownButtonText();
            _warningDialogCountdownTimer.Start();

            Debug.WriteLine($"? Reporting day information displayed");
            Debug.WriteLine($"?? Auto-closing in 3 seconds...");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error showing reporting day information: {ex.Message}");
        }
    }

    private void WarningDialogCountdownTimer_Tick(object? sender, EventArgs e)
    {
        _warningDialogCountdown--;
        UpdateWarningDialogCountdownButtonText();

        if (_warningDialogCountdown <= 0)
        {
            _warningDialogCountdownTimer.Stop();
            CloseReportingDayWarning();
        }
    }

    private void UpdateWarningDialogCountdownButtonText()
    {
        if (_warningDialogCountdown > 0)
        {
            WarningOkButton.Content = $"OK, I UNDERSTAND ({_warningDialogCountdown}s)";
        }
        else
        {
            WarningOkButton.Content = "OK, I UNDERSTAND";
        }
    }

    private void WarningOkButton_Click(object sender, RoutedEventArgs e)
    {
        _warningDialogCountdownTimer.Stop();
        CloseReportingDayWarning();
    }

    private void CloseReportingDayWarning()
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"--------------------");
            Debug.WriteLine($"? REPORTING DAY INFORMATION CLOSED");
            Debug.WriteLine($"   User clicked OK or auto-closed");
            Debug.WriteLine($"--------------------");

            ReportingDayWarningOverlay.Visibility = Visibility.Collapsed;
            EnsureRfidFocus("ReportingDayInformationClosed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error closing reporting day information: {ex.Message}");
        }
    }

    public async Task ShowOnlineClassInfoDialogAsync(string holidayName, DateTime currentDate)
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"----------------------------------------");
            Debug.WriteLine($"?? SHOWING ONLINE CLASS INFORMATION");
            Debug.WriteLine($"   Holiday: {holidayName}");
            Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd}");
            Debug.WriteLine($"----------------------------------------");

            OnlineClassHolidayText.Text = holidayName;
            OnlineClassDateText.Text = $"Date: {currentDate:dddd, MMMM dd, yyyy}";
            OnlineClassHolidayTypeText.Text = "Holiday: online_class";

            OnlineClassInfoOverlay.Visibility = Visibility.Visible;

            _onlineClassCountdown = 3;
            UpdateOnlineClassButtonText();
            _onlineClassCountdownTimer.Start();

            Debug.WriteLine($"? Online class information displayed");
            Debug.WriteLine($"?? Auto-closing in 3 seconds...");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error showing online class information: {ex.Message}");
        }
    }

    private void OnlineClassCountdownTimer_Tick(object? sender, EventArgs e)
    {
        _onlineClassCountdown--;
        UpdateOnlineClassButtonText();

        if (_onlineClassCountdown <= 0)
        {
            _onlineClassCountdownTimer.Stop();
            CloseOnlineClassDialog();
        }
    }

    private void UpdateOnlineClassButtonText()
    {
        if (_onlineClassCountdown > 0)
        {
            OnlineClassOkButton.Content = $"? OK, I UNDERSTAND ({_onlineClassCountdown}s)";
        }
        else
        {
            OnlineClassOkButton.Content = "? OK, I UNDERSTAND";
        }
    }

    private void OnlineClassOkButton_Click(object sender, RoutedEventArgs e)
    {
        _onlineClassCountdownTimer.Stop();
        CloseOnlineClassDialog();
    }

    private void CloseOnlineClassDialog()
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"--------------------");
            Debug.WriteLine($"? ONLINE CLASS INFORMATION CLOSED");
            Debug.WriteLine($"   User clicked OK or auto-closed");
            Debug.WriteLine($"--------------------");

            OnlineClassInfoOverlay.Visibility = Visibility.Collapsed;
            EnsureRfidFocus("OnlineClassInformationClosed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error closing online class information: {ex.Message}");
        }
    }
}
