using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using RAMSOfficial.Models;
using System.Diagnostics;
using RAMSOfficial.Services;
using System.Windows.Threading;
using System.Threading;
using RAMSOfficial.Helpers;
using System.IO;
using System.Windows.Media.Imaging;

namespace RAMSOfficial.ViewModels;

public enum AppState
{
    WaitingForReader,
    WaitingForTap,
    ShowingProfile
}

public class MainViewModel : ViewModelBase
{
    private string _rfidInput = string.Empty;
    private Employee? _currentEmployee;
    private string _statusMessage = "Initializing...";
    private string _statusColor = "#FFC107";
    private bool _isProcessing = false;
    private string _lastTapType = string.Empty;
    private DateTime? _lastTapTimeNullable;
    private ObservableCollection<AttendanceLog> _todayLogs = new();
    private ObservableCollection<RecentAttendanceItem> _recentAttendance = new();
    private AppState _currentState = AppState.WaitingForReader;
    private bool _isReaderConnected = false;
    private bool _isManualInputMode = false;
    private int _countdown = 0;
    private DispatcherTimer? _countdownTimer;
    private DateTime _lastInputTime = DateTime.MinValue;
    private DateTime _lastTapTimeCooldown = DateTime.MinValue;
    private const int TAP_COOLDOWN_SECONDS = 3; // Changed from 5 to 3 seconds
    private bool _isInCooldown = false;
    
    // Track tap count per employee per day to show "complete" message only on 3rd+ tap
    private readonly Dictionary<string, int> _employeeTapCountToday = new();

    // Track last tap time per employee (using RFID code) for the 5-minute timeout
    private readonly Dictionary<string, DateTime> _employeeLastTapTime = new();
    private const int EMPLOYEE_TAP_COOLDOWN_MINUTES = 5;

    public string RfidInput
    {
        get => _rfidInput;
        set
        {
            if (SetProperty(ref _rfidInput, value))
            {
                // Track last input time for reader detection
                _lastInputTime = DateTime.Now;
            }
        }
    }

    public Employee? CurrentEmployee
    {
        get => _currentEmployee;
        set => SetProperty(ref _currentEmployee, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    public string LastTapType
    {
        get => _lastTapType;
        set => SetProperty(ref _lastTapType, value);
    }

    public DateTime? LastTapTime
    {
        get => _lastTapTimeNullable;
        set => SetProperty(ref _lastTapTimeNullable, value);
    }

    public ObservableCollection<AttendanceLog> TodayLogs
    {
        get => _todayLogs;
        set => SetProperty(ref _todayLogs, value);
    }

    public ObservableCollection<RecentAttendanceItem> RecentAttendance
    {
        get => _recentAttendance;
        set => SetProperty(ref _recentAttendance, value);
    }

    public AppState CurrentState
    {
        get => _currentState;
        set
        {
            if (SetProperty(ref _currentState, value))
            {
                OnPropertyChanged(nameof(IsWaitingForReader));
                OnPropertyChanged(nameof(IsWaitingForTap));
                OnPropertyChanged(nameof(IsShowingProfile));
            }
        }
    }

    public bool IsReaderConnected
    {
        get => _isReaderConnected;
        set
        {
            if (SetProperty(ref _isReaderConnected, value))
            {
                UpdateState();
            }
        }
    }

    public bool IsManualInputMode
    {
        get => _isManualInputMode;
        set => SetProperty(ref _isManualInputMode, value);
    }

    public int Countdown
    {
        get => _countdown;
        set => SetProperty(ref _countdown, value);
    }

    public bool IsWaitingForReader => CurrentState == AppState.WaitingForReader;
    public bool IsWaitingForTap => CurrentState == AppState.WaitingForTap;
    public bool IsShowingProfile => CurrentState == AppState.ShowingProfile;

    public RelayCommand ProcessRfidCommand { get; }
    public RelayCommand SimulateTapCommand { get; }
    public RelayCommand CheckReaderCommand { get; }
    public RelayCommand ToggleManualInputCommand { get; }

    public MainViewModel()
    {
        ProcessRfidCommand = new RelayCommand(async () => await ProcessRfidAsync());
        SimulateTapCommand = new RelayCommand(async () => await SimulateTapAsync());
        CheckReaderCommand = new RelayCommand(async () => await CheckReaderConnectionAsync());
        ToggleManualInputCommand = new RelayCommand(async () => await ToggleManualInputAsync());
        
        // Setup countdown timer
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
        
        Debug.WriteLine("?? Initializing MainViewModel...");
        Debug.WriteLine($"   Initial CurrentState: {CurrentState}");
        
        // Start in WaitingForTap state (MainWindow only shows when reader is connected)
        CurrentState = AppState.WaitingForTap;
        IsReaderConnected = true; // MainWindow only opens when reader is connected
        StatusMessage = "Ready - Tap your RFID card";
        StatusColor = "#0066CC";
        
        Debug.WriteLine($"   Set CurrentState to: {CurrentState}");
        Debug.WriteLine($"   Set IsReaderConnected to: {IsReaderConnected}");
        
        // Load initial data
        Task.Run(async () =>
        {
            await CheckReaderConnectionAsync();
            await LoadRecentEmployeesAsync();
        });
    }

    private void OnReaderConnectionChanged(object? sender, bool isConnected)
    {
        // This event handler is no longer needed - App.xaml.cs handles window switching
        Debug.WriteLine($"?? OnReaderConnectionChanged called but ignored (App.xaml.cs handles this)");
        Debug.WriteLine($"   IsConnected: {isConnected}");
    }

    private void OnSimpleReaderConnectionChanged(object? sender, bool isConnected)
    {
        // This event handler is no longer needed - App.xaml.cs handles window switching
        Debug.WriteLine($"?? OnSimpleReaderConnectionChanged called but ignored (App.xaml.cs handles this)");
        Debug.WriteLine($"   IsConnected: {isConnected}");
    }

    private async Task StartReaderCheckAsync()
    {
        // This method is no longer needed - App.xaml.cs handles reader detection
        Debug.WriteLine("?? StartReaderCheckAsync called but not needed - App.xaml.cs handles detection");
        await Task.CompletedTask;
    }

    private async Task CheckReaderConnectionAsync()
    {
        try
        {
            // Database connection check
            var dbService = App.DatabaseService;
            await dbService.GetEmployeeByRfidAsync("TEST_CONNECTION");
            
            // Reader connection is determined by USB detection service
            // But we can override with manual mode
            if (IsManualInputMode)
            {
                IsReaderConnected = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Database check failed: {ex.Message}");
            // Don't change reader connection status based on DB errors
        }
    }

    private async Task ToggleManualInputAsync()
    {
        IsManualInputMode = !IsManualInputMode;
        IsReaderConnected = true; // Always consider connected when in manual mode
        await Task.CompletedTask;
    }

    private void UpdateState()
    {
        if (!IsReaderConnected && !IsManualInputMode)
        {
            CurrentState = AppState.WaitingForReader;
            StatusMessage = "Waiting for RFID Card Reader...";
            StatusColor = "#FFC107";
        }
        else
        {
            CurrentState = AppState.WaitingForTap;
            StatusMessage = IsManualInputMode ? "Ready - Enter RFID manually" : "Ready - Tap your RFID card";
            StatusColor = "#0066CC";
        }
    }

    private async Task LoadRecentEmployeesAsync()
    {
        try
        {
            // Source of truth: PostgreSQL attendance_logs (local offline DB instance).
            var targetDate = TimeChangerWindow.GetCurrentTime().Date;
            var attendanceItems = await App.DatabaseService.GetRecentAttendanceItemsForDateAsync(targetDate, 30);

            // Fallback only if direct DB query returns nothing.
            if (attendanceItems.Count == 0 && App.AttendanceSyncService != null)
            {
                attendanceItems = await App.AttendanceSyncService.GetRecentAttendanceAsync(30);
            }

            // Only show real attendance taps in the recent feed.
            // Exclude absent/leave/admin/other non-attendance statuses.
            attendanceItems = attendanceItems
                .Where(ShouldIncludeRecentAttendanceItem)
                .ToList();

            // Enrich only missing fields from ScheduleCache when offline.
            // Do NOT blindly overwrite names from query results, otherwise a stale/mismatched
            // cache entry can make multiple rows look like the same employee.
            if (!App.HybridDatabase.IsOnline && App.ScheduleCache.IsLoaded)
            {
                foreach (var item in attendanceItems)
                {
                    if (item.EmployeeId <= 0) continue;

                    var cachedEmp = App.ScheduleCache.GetEmployeeById(item.EmployeeId);
                    if (cachedEmp != null)
                    {
                        if (string.IsNullOrWhiteSpace(item.FullName) ||
                            item.FullName.StartsWith("Employee #", StringComparison.OrdinalIgnoreCase))
                        {
                            item.FullName = cachedEmp.FullName;
                        }

                        if (string.IsNullOrWhiteSpace(item.Department))
                        {
                            item.Department = cachedEmp.Department ?? "";
                        }

                        if (!string.IsNullOrEmpty(item.FullName) && item.FullName.Length > 0)
                        {
                            item.Initial = item.FullName.Substring(0, 1).ToUpper();
                        }

                        if (string.IsNullOrWhiteSpace(item.PhotoPath) &&
                            !string.IsNullOrEmpty(cachedEmp.PhotoPath))
                        {
                            item.PhotoPath = cachedEmp.PhotoPath;
                        }
                    }
                }

            // If online, remove placeholder/unknown entries so deleted employees
            // from the website don't continue to appear via stale local data.
            if (App.HybridDatabase.IsOnline)
            {
                attendanceItems = attendanceItems
                    .Where(x => !string.IsNullOrWhiteSpace(x.FullName) &&
                                !x.FullName.StartsWith("Employee #", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            }

            attendanceItems = attendanceItems
                .Where(x => x.EmployeeId > 0 || !string.IsNullOrWhiteSpace(x.FullName))
                .GroupBy(x => x.EmployeeId > 0
                    ? $"ID:{x.EmployeeId}"
                    : $"NAME:{x.FullName.Trim().ToUpperInvariant()}")
                .Select(g =>
                {
                    var ordered = g.OrderByDescending(x => x.LogTime).ToList();
                    var latest = ordered[0];

                    // Ensure TimeIn/TimeOut are always derived per employee,
                    // even if fallback data is per-log.
                    latest.TimeIn ??= ordered
                        .Where(x => string.Equals(x.LogType, "IN", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x.LogTime)
                        .Select(x => (DateTime?)x.LogTime)
                        .FirstOrDefault();

                    latest.TimeOut ??= ordered
                        .Where(x => string.Equals(x.LogType, "OUT", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.LogTime)
                        .Select(x => (DateTime?)x.LogTime)
                        .FirstOrDefault();

                    return latest;
                })
                .OrderByDescending(x => x.LogTime)
                .Take(30)
                .ToList();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                RecentAttendance = new ObservableCollection<RecentAttendanceItem>(attendanceItems);
            });

            _ = LoadRecentPhotosAsync(attendanceItems);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading recent attendance: {ex.Message}");
        }
    }

    private static bool ShouldIncludeRecentAttendanceItem(RecentAttendanceItem item)
    {
        var status = (item.AttendanceStatus ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("_", "-")
            .Replace(" ", "-");

        // Keep only true attendance outcomes.
        return status is "on-time" or "late" or "undertime";
    }

    private async Task LoadRecentPhotosAsync(List<RecentAttendanceItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                BitmapImage? photo = null;

                if (item.EmployeeId > 0)
                {
                    var photoBytes = await App.DatabaseService.GetEmployeePhotoBytesAsync(item.EmployeeId);
                    if (photoBytes != null && photoBytes.Length > 0)
                    {
                        using var stream = new MemoryStream(photoBytes);
                        var fromDb = new BitmapImage();
                        fromDb.BeginInit();
                        fromDb.CacheOption = BitmapCacheOption.OnLoad;
                        fromDb.DecodePixelWidth = 80;
                        fromDb.StreamSource = stream;
                        fromDb.EndInit();
                        fromDb.Freeze();
                        photo = fromDb;
                    }
                }

                if (photo == null && !string.IsNullOrWhiteSpace(item.PhotoPath))
                    photo = await Helpers.AsyncImageLoader.LoadAsync(item.PhotoPath, decodePixelWidth: 80);

                if (photo != null)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        item.Photo = photo;
                        // Force collection update so the UI refreshes
                        var idx = -1;
                        for (int i = 0; i < RecentAttendance.Count; i++)
                        {
                            if (RecentAttendance[i].EmployeeId == item.EmployeeId &&
                                RecentAttendance[i].LogTime == item.LogTime)
                            {
                                idx = i;
                                break;
                            }
                        }
                        if (idx >= 0)
                        {
                            RecentAttendance.RemoveAt(idx);
                            RecentAttendance.Insert(idx, item);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Photo load failed for {item.FullName}: {ex.Message}");
            }
        }
    }

    public async Task RefreshRecentAttendanceAsync()
    {
        // Public method for auto-refresh
        await LoadRecentEmployeesAsync();
    }

    private async Task ProcessRfidAsync()
    {
        if (IsProcessing || string.IsNullOrWhiteSpace(RfidInput))
        {
            Debug.WriteLine($"?? Processing blocked - IsProcessing: {IsProcessing}, RfidInput empty: {string.IsNullOrWhiteSpace(RfidInput)}");
            return;
        }

        var rfidCode = RfidInput.Trim();
        
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????");
        Debug.WriteLine($"  ?? PROCESSING RFID CARD TAP                         ");
        Debug.WriteLine($"  Code: {rfidCode,-44} ");
        Debug.WriteLine($"???????????????????????????????????????");

        // Use helper to process with validation
        var success = await RfidInputHelper.ProcessRfidInputAsync(rfidCode, async (code) =>
        {
            return await ProcessRfidInternalAsync(code);
        });

        if (!success)
        {
            RfidInput = string.Empty;
        }
    }

    private async Task<bool> ProcessRfidInternalAsync(string rfidCode)
    {
        Debug.WriteLine("");
        Debug.WriteLine("???????????????????????????????????????????????????????????");
        Debug.WriteLine("?? MAIN VIEW MODEL - ProcessRfidInternalAsync");
        Debug.WriteLine("???????????????????????????????????????????????????????????");
        Debug.WriteLine($"?? RFID Code: '{rfidCode}'");
        Debug.WriteLine($"?? Current Connection Status: {(App.HybridDatabase.IsOnline ? "ONLINE" : "OFFLINE")}");
        Debug.WriteLine($"?? Network Status: {App.HybridDatabase.ConnectionStatus}");
        Debug.WriteLine($"? Current Time: {TimeChangerWindow.GetCurrentTime():yyyy-MM-dd HH:mm:ss}");
        Debug.WriteLine($"? Time Mode: {(TimeChangerWindow.IsTimeOverridden() ? "CUSTOM OVERRIDE" : "Real Time")}");
        Debug.WriteLine($"???????????????????????????????????????????????????????????");
        
        // Initialize RFID attempt logging service
        var attemptLogger = new RfidAttemptLoggingService(
            App.DatabaseService.GetConnectionString()
        );

        // Declare employee at method scope for error handling
        Employee? employee = null;

        // Check cooldown - Use REAL TIME (DateTime.Now) for cooldown, not custom time
        var timeSinceLastTap = DateTime.Now - _lastTapTimeCooldown;
        if (_isInCooldown && timeSinceLastTap.TotalSeconds < TAP_COOLDOWN_SECONDS)
        {
            var remainingSeconds = TAP_COOLDOWN_SECONDS - (int)timeSinceLastTap.TotalSeconds;
            
            Debug.WriteLine($"?? COOLDOWN ACTIVE - {remainingSeconds} seconds remaining");
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                
                CustomMessageDialog.ShowWarning(
                    $"Please wait {remainingSeconds} second(s) before tapping again.",
                    "This prevents duplicate entries.\n\n" +
                    "Your previous tap is still being processed.",
                    countdown: 3
                );
                
                // Restore focus
                if (rfidTextBox != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() =>
                        {
                            rfidTextBox.Focus();
                            System.Windows.Input.Keyboard.Focus(rfidTextBox);
                        })
                    );
                }
            });
            
            // Log duplicate attempt
            await attemptLogger.LogDuplicateAttemptAsync(rfidCode, 0, "Unknown");
            
            RfidInput = string.Empty;
            return false;
        }

        IsProcessing = true;
        _lastTapTimeCooldown = DateTime.Now; // Use REAL TIME for cooldown tracking
        _isInCooldown = true;
        
        StatusMessage = "Processing RFID...";
        StatusColor = "#FFC107";

        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"🔍 QUERYING DATABASE FOR EMPLOYEE...");
            Debug.WriteLine($"   RFID Code: '{rfidCode}'");
            Debug.WriteLine($"   Calling: MirrorEngine.RfidLookupAsync (single authority)");

            // Log current time mode
            var currentTime = TimeChangerWindow.GetCurrentTime();
            var isTimeOverrideActive = TimeChangerWindow.IsTimeOverridden();
            Debug.WriteLine($"⏰ Current Time: {currentTime:yyyy-MM-dd HH:mm:ss}");
            Debug.WriteLine($"⏰  Time Mode: {(isTimeOverrideActive ? "CUSTOM OVERRIDE" : "Real Time")}");

            // ONLINE: Query database directly — the single source of truth.
            // OFFLINE: Fall back to RAM cache → local SQLite.
            Debug.WriteLine($"");
            Debug.WriteLine($"🔍 Looking up employee (Online={App.HybridDatabase.IsOnline})...");

            if (App.HybridDatabase.IsOnline)
            {
                try
                {
                    employee = await App.DatabaseService.GetEmployeeByRfidAsync(rfidCode);
                    if (employee != null)
                    {
                        App.ScheduleCache?.CacheEmployee(employee); // keep cache warm
                        Debug.WriteLine($"🔍 Employee resolved from database.");
                    }
                }
                catch (Exception lookupEx)
                {
                    Debug.WriteLine($"🔍 database lookup failed — falling back to local: {lookupEx.Message}");
                }
            }

            // Fallback (offline or database failed): RAM cache → local SQLite
            if (employee == null)
            {
                employee = App.ScheduleCache?.GetEmployeeByRfid(rfidCode);
                if (employee == null)
                {
                    try
                    {
                        var localDb = new LocalDatabaseService();
                        employee = await localDb.GetEmployeeByRfidAsync(rfidCode);
                        if (employee != null)
                            Debug.WriteLine($"🔍 Employee resolved from local SQLite.");
                    }
                    catch (Exception lookupEx)
                    {
                        Debug.WriteLine($"🔍 LocalDB lookup error: {lookupEx.Message}");
                    }
                }
                else
                {
                    Debug.WriteLine($"🔍 Employee resolved from RAM cache.");
                }
            }
            Debug.WriteLine($"");
            Debug.WriteLine($"🔍 RFID Lookup returned:");
            Debug.WriteLine($"   Result: {(employee != null ? "Employee object" : "NULL")}");

            if (employee == null)
            {
                Debug.WriteLine($"");
                Debug.WriteLine($"????????????????????????????????????????????????????????????");
                Debug.WriteLine($"? EMPLOYEE NOT FOUND");
                Debug.WriteLine($"????????????????????????????????????????????????????????????");
                Debug.WriteLine($"   RFID Code: '{rfidCode}'");
                Debug.WriteLine($"   Result: NULL (no matching employee in database)");
                Debug.WriteLine($"   Database Mode: {(App.HybridDatabase.IsOnline ? "ONLINE (database)" : "OFFLINE (LocalDB)")}")
;
                Debug.WriteLine($"????????????????????????????????????????????????????????????");
                Debug.WriteLine($"");
                
                // ? UNKNOWN RFID - Log the attempt
                await attemptLogger.LogUnknownRfidAsync(rfidCode);
                
                StatusMessage = "? Employee not found!";
                StatusColor = "#F44336";
                
                Debug.WriteLine($"? No employee found with RFID: {rfidCode}");
                
                // Show error notification
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                    
                    CustomMessageDialog.ShowError(
                        "Unknown RFID Card",
                        $"RFID Code: {rfidCode}\n\n" +
                        "This card is not registered in the system.\n\n" +
                        "Please contact the administrator to register your RFID card.\n\n" +
                        "This attempt has been logged for security purposes.",
                        countdown: 5
                    );
                    
                    // Restore focus after dialog
                    if (rfidTextBox != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() =>
                            {
                                rfidTextBox.Focus();
                                System.Windows.Input.Keyboard.Focus(rfidTextBox);
                            })
                        );
                    }
                });
                
                await Task.Delay(100);
                ResetToReadyState();
                RfidInput = string.Empty;
                
                // Reset cooldown for unknown RFID
                await Task.Delay(2000);
                _isInCooldown = false;
                
                return false;
            }

            Debug.WriteLine($"");
            Debug.WriteLine($"????????????????????????????????????????????????????????????");
            Debug.WriteLine($"? EMPLOYEE FOUND!");
            Debug.WriteLine($"????????????????????????????????????????????????????????????");
            Debug.WriteLine($"   ID: {employee.EmployeeId}");
            Debug.WriteLine($"   Name: {employee.FullName}");
            Debug.WriteLine($"   Department: {employee.Department}");
            Debug.WriteLine($"   RFID: {employee.RfidCode}");
            Debug.WriteLine($"   Employment Status: {employee.EmploymentStatus}");
            Debug.WriteLine($"   Is Active: {employee.IsActive}");
            Debug.WriteLine($"   Database Mode: {(App.HybridDatabase.IsOnline ? "ONLINE (database)" : "OFFLINE (LocalDB)")}");
            Debug.WriteLine($"????????????????????????????????????????????????????????????");
            Debug.WriteLine($"");

            RfidInputHelper.LogRfidAttempt(rfidCode, true, employee.FullName);

            // Check if employee is active
            if (!employee.IsActive)
            {
                // ?? INACTIVE EMPLOYEE - Log the attempt
                await attemptLogger.LogInactiveEmployeeAsync(rfidCode, employee.EmployeeId, employee.FullName);
                
                StatusMessage = "?? Account Inactive!";
                StatusColor = "#F44336";
                
                Debug.WriteLine($"?? Employee {employee.FullName} is INACTIVE");
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                    
                    CustomMessageDialog.ShowWarning(
                        "Account Inactive",
                        $"Employee: {employee.FullName}\n" +
                        $"ID: {employee.SchoolId ?? "N/A"}\n\n" +
                        "Your account is currently inactive.\n\n" +
                        "Please contact HR or the administrator to reactivate your account.",
                        countdown: 5
                    );
                    
                    // Restore focus
                    if (rfidTextBox != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() =>
                            {
                                rfidTextBox.Focus();
                                System.Windows.Input.Keyboard.Focus(rfidTextBox);
                            })
                        );
                    }
                });
                
                await Task.Delay(100);
                ResetToReadyState();
                RfidInput = string.Empty;
                
                // Reset cooldown for inactive employee
                await Task.Delay(2000);
                _isInCooldown = false;
                
                return false;
            }

            if (employee.StartDate.HasValue && employee.StartDate.Value.Date > currentTime.Date)
            {
                await attemptLogger.LogBlockedAttemptAsync(
                    rfidCode,
                    employee.EmployeeId,
                    employee.FullName,
                    "START_DATE_PENDING"
                );

                StatusMessage = "?? Start date not reached";
                StatusColor = "#F59E0B";

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;

                    CustomMessageDialog.ShowWarning(
                        "Start Date Not Reached",
                        $"Employee: {employee.FullName}\n" +
                        $"ID: {employee.SchoolId ?? "N/A"}\n\n" +
                        $"This employee is scheduled to start on {employee.StartDate:MMMM dd, yyyy}.\n\n" +
                        "Attendance can begin on the start date.",
                        countdown: 5
                    );

                    if (rfidTextBox != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() =>
                            {
                                rfidTextBox.Focus();
                                System.Windows.Input.Keyboard.Focus(rfidTextBox);
                            })
                        );
                    }
                });

                await Task.Delay(100);
                ResetToReadyState();
                RfidInput = string.Empty;

                await Task.Delay(2000);
                _isInCooldown = false;

                return false;
            }

            // +++ NEW: 5-minute cooldown for the same employee +++
            IEnumerable<AttendanceLog>? preFetchedTodayLogs = null;
            bool hasExistingTimeInAndOut = false;

            try
            {
                preFetchedTodayLogs = await App.HybridDatabase.GetTodayAttendanceForDateAsync(employee.EmployeeId, currentTime.Date);
                hasExistingTimeInAndOut = preFetchedTodayLogs.Any(l => l.LogType == "IN") &&
                                          preFetchedTodayLogs.Any(l => l.LogType == "OUT");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠ Failed pre-check for existing IN/OUT logs: {ex.Message}");
            }

            if (_employeeLastTapTime.TryGetValue(rfidCode, out var lastEmployeeTapTime))
            {
                var timeSinceEmployeeTap = DateTime.Now - lastEmployeeTapTime;
                // If employee already has both TIME IN and TIME OUT today,
                // do NOT apply 5-minute repetitive-tap block.
                if (!isTimeOverrideActive && !hasExistingTimeInAndOut && timeSinceEmployeeTap.TotalMinutes < EMPLOYEE_TAP_COOLDOWN_MINUTES)
                {
                    var remainingTime = TimeSpan.FromMinutes(EMPLOYEE_TAP_COOLDOWN_MINUTES) - timeSinceEmployeeTap;
                    var remainingMins = (int)remainingTime.TotalMinutes;
                    var remainingSecs = remainingTime.Seconds;

                    var timeStr = remainingMins > 0 ? $"{remainingMins} min and {remainingSecs} sec" : $"{remainingSecs} sec";

                    Debug.WriteLine($"?? EMPLOYEE COOLDOWN ACTIVE - {timeStr} remaining for {employee.FullName}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;

                        CustomMessageDialog.ShowWarning(
                            "Please wait before tapping again",
                            $"Employee: {employee.FullName}\n\n" +
                            $"To prevent accidental double-tapping, there is a 5-minute wait between scans.\n\n" +
                            $"Please wait {timeStr}.",
                            countdown: 3
                        );

                        if (rfidTextBox != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    rfidTextBox.Focus();
                                    System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                })
                            );
                        }
                    });

                    await attemptLogger.LogDuplicateAttemptAsync(rfidCode, employee.EmployeeId, employee.FullName);

                    await Task.Delay(100);
                    ResetToReadyState();
                    RfidInput = string.Empty;
                    await Task.Delay(2000);
                    _isInCooldown = false;
                    return false;
                }
            }

            Debug.WriteLine($"? Employee Found!");
            Debug.WriteLine($"   ID: {employee.EmployeeId}");
            Debug.WriteLine($"   Name: {employee.FullName}");
            Debug.WriteLine($"   Department: {employee.Department}");
            Debug.WriteLine($"   RFID: {employee.RfidCode}");

            RfidInputHelper.LogRfidAttempt(rfidCode, true, employee.FullName);

            // ???????????????????????????????????????????????
            // PRIORITY 0 (RAM): INSTANT SCHEDULE PRE-CHECK — OFFLINE ONLY
            // When ONLINE: skip entirely. database is the source of truth.
            //   EmploymentStatusValidationService (Priority 4) calls
            //   GetClassSchedulesForDateAsync which queries database directly,
            //   so the RAM cache cannot produce a false "no schedule" block.
            // When OFFLINE: use the RAM cache as a fast-path guard so the
            //   kiosk responds instantly without any network call.
            // ???????????????????????????????????????????????
            if (App.ScheduleCache.IsLoaded && App.HybridDatabase is { IsOnline: false })
            {
                var preCheck = App.ScheduleCache.ValidateAttendance(employee!);
                Debug.WriteLine($"");
                Debug.WriteLine($"?? SCHEDULE PRE-CHECK (RAM):");
                Debug.WriteLine($"   Has Schedule: {preCheck.HasSchedule}");
                Debug.WriteLine($"   Is Part-Time: {preCheck.IsPartTime}");
                Debug.WriteLine($"   Should Warn: {preCheck.ShouldWarn}");
                if (preCheck.EarliestStart.HasValue)
                    Debug.WriteLine($"   Window: {preCheck.EarliestStart:hh\\:mm} - {preCheck.LatestEnd:hh\\:mm}");

                if (preCheck.ShouldWarn)
                {
                    // Safety fallback: before hard-blocking, verify local mirror tables
                    // (class_schedules + exam_schedules) to avoid false NO_SCHEDULE
                    // when RAM cache is stale during recent online/offline transitions.
                    var hasMirrorSchedule = false;
                    try
                    {
                        var localDb = new LocalDatabaseService();
                        hasMirrorSchedule = await localDb.HasAnyScheduleForDateAsync(employee.EmployeeId, currentTime.Date);
                    }
                    catch (Exception localEx)
                    {
                        Debug.WriteLine($"⚠ Local mirror schedule fallback failed: {localEx.Message}");
                    }

                    if (hasMirrorSchedule)
                    {
                        Debug.WriteLine($"✅ NO_SCHEDULE pre-check bypassed: local mirror has class/exam schedule for {employee.FullName}");
                    }
                    else
                    {
                    // NO_SCHEDULE for Part-Time → instant warning, skip expensive DB calls
                    await attemptLogger.LogBlockedAttemptAsync(
                        rfidCode, employee.EmployeeId, employee.FullName, "NO_SCHEDULE_PT");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;

                        CustomMessageDialog.ShowScheduleWarning(
                            "No Schedule Today",
                            $"Employee: {employee.FullName}\n" +
                            $"Status: {employee.EmploymentStatus}\n" +
                            $"Day: {DateTime.Today:dddd, MMMM dd, yyyy}\n\n" +
                            "You have no classes or duties scheduled for today.\n\n" +
                            "Part-Time employees can only tap in on days they are scheduled.",
                            countdown: 5
                        );

                        if (rfidTextBox != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    rfidTextBox.Focus();
                                    System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                })
                            );
                        }
                    });

                    await Task.Delay(100);
                    ResetToReadyState();
                    RfidInput = string.Empty;
                    await Task.Delay(2000);
                    _isInCooldown = false;
                    return false;
                    }
                }
            }

            // ???????????????????????????????????????????????
            // PRIORITY 1: HOLIDAY/SPECIAL DAY VALIDATION (HIGHEST)
            // ???????????????????????????????????????????????
            Debug.WriteLine($"");
            Debug.WriteLine($"??? CHECKING HOLIDAY CALENDAR...");
            
            var holidayValidator = new HolidayValidationService(
                App.DatabaseService.GetConnectionString()
            );
            
            var holidayValidation = await holidayValidator.ValidateHolidayAttendanceAsync(
                employee.EmployeeId,
                employee.EmploymentStatus,
                employee.EmploymentType,
                employee.StaffType,  // ? Added staffType parameter
                currentTime
            );

            if (!holidayValidation.IsAllowed)
            {
                // ? HOLIDAY BLOCK - Show warning and prevent attendance
                await attemptLogger.LogBlockedAttemptAsync(
                    rfidCode,
                    employee.EmployeeId,
                    employee.FullName,
                    holidayValidation.Reason ?? "HOLIDAY_BLOCK"
                );
                
                Debug.WriteLine($"? ATTENDANCE BLOCKED BY HOLIDAY CALENDAR!");
                Debug.WriteLine($"   Holiday: {holidayValidation.HolidayName}");
                Debug.WriteLine($"   Type: {holidayValidation.HolidayType}");
                Debug.WriteLine($"   Reason: {holidayValidation.Reason}");
                Debug.WriteLine($"   Message: {holidayValidation.Message}");
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                    
                    // Show holiday warning dialog
                    CustomMessageDialog.ShowWarning(
                        holidayValidation.Message ?? "Attendance blocked.",
                        $"Date: {currentTime:dddd, MMMM dd, yyyy}\n" +
                        $"Holiday: {holidayValidation.HolidayName}\n" +
                        $"Type: {holidayValidation.HolidayType}\n\n" +
                        $"Reason: {ReasonTextFormatter.ToFormal(holidayValidation.Reason)}\n\n" +
                        "No attendance will be recorded.",
                        countdown: 5
                    );
                    
                    // Restore focus
                    if (rfidTextBox != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() =>
                            {
                                rfidTextBox.Focus();
                                System.Windows.Input.Keyboard.Focus(rfidTextBox);
                            })
                        );
                    }
                });
                
                await Task.Delay(100);
                ResetToReadyState();
                RfidInput = string.Empty;
                
                // Reset cooldown
                await Task.Delay(2000);
                _isInCooldown = false;
                
                return false;
            }

            // ? HOLIDAY ALLOWS ATTENDANCE (with special handling)
            if (holidayValidation.IsHolidayWithAttendance)
            {
                Debug.WriteLine($"? HOLIDAY ATTENDANCE ALLOWED!");
                Debug.WriteLine($"   Mode: {holidayValidation.AttendanceMode}");
                Debug.WriteLine($"   Holiday: {holidayValidation.HolidayName}");
                Debug.WriteLine($"   Type: {holidayValidation.HolidayType}");
            }

            // ???????????????????????????????????????????????
            // PRIORITY 2: LEAVE VALIDATION (NEW!)
            // ???????????????????????????????????????????????
            Debug.WriteLine($"");
            Debug.WriteLine($"?? CHECKING APPROVED LEAVE STATUS...");
            
            var leaveValidator = new LeaveValidationService(
                App.DatabaseService.GetConnectionString()
            );
            
            var leaveValidation = await leaveValidator.ValidateLeaveStatusAsync(
                employee.EmployeeId,
                currentTime
            );

            if (!leaveValidation.IsAllowed && leaveValidation.HasApprovedLeave)
            {
                // ? APPROVED LEAVE - Block attendance
                await attemptLogger.LogBlockedAttemptAsync(
                    rfidCode,
                    employee.EmployeeId,
                    employee.FullName,
                    "APPROVED_LEAVE"
                );
                
                Debug.WriteLine($"? ATTENDANCE BLOCKED - APPROVED LEAVE!");
                Debug.WriteLine($"   Request ID: {leaveValidation.LeaveRequestId}");
                Debug.WriteLine($"   Date: {currentTime:yyyy-MM-dd}");
                Debug.WriteLine($"   Reason: {leaveValidation.LeaveReason}");
                
                // Build leave details message
                var leaveDetails = $"Employee: {employee.FullName}\n" +
                                 $"Date: {currentTime:dddd, MMMM dd, yyyy}\n\n" +
                                 $"Reason: {ReasonTextFormatter.ToFormal(leaveValidation.LeaveReason ?? "Not specified")}\n";
                
                if (leaveValidation.LeaveStartTime.HasValue && leaveValidation.LeaveEndTime.HasValue)
                {
                    leaveDetails += $"Duration: {leaveValidation.LeaveStartTime:hh\\:mm} - {leaveValidation.LeaveEndTime:hh\\:mm}\n";
                }
                
                if (leaveValidation.ReviewedAt.HasValue)
                {
                    leaveDetails += $"\nApproved: {leaveValidation.ReviewedAt:yyyy-MM-dd HH:mm}\n";
                }
                
                leaveDetails += "\n? This employee is on approved leave.\n" +
                               "No attendance will be recorded.";
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                    
                    // Show leave warning dialog
                    CustomMessageDialog.ShowWarning(
                        "Approved Leave",
                        leaveDetails,
                        countdown: 5
                    );
                    
                    // Restore focus
                    if (rfidTextBox != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Background,
                            new Action(() =>
                            {
                                rfidTextBox.Focus();
                                System.Windows.Input.Keyboard.Focus(rfidTextBox);
                            })
                        );
                    }
                });
                
                await Task.Delay(100);
                ResetToReadyState();
                RfidInput = string.Empty;
                
                // Reset cooldown
                await Task.Delay(2000);
                _isInCooldown = false;
                
                return false;
            }

            Debug.WriteLine($"? No approved leave found - Continue validation");

            // ???????????????????????????????????????????????????????????????????
            // PRIORITY 2.5: MISSED LOG VALIDATION (NEW! - Between Leave and Substitution)
            // ???????????????????????????????????????????????????????????????????
            Debug.WriteLine($"");
            Debug.WriteLine($"?? CHECKING MISSED LOG REQUESTS...");
            
            var missedLogValidator = new MissedLogValidationService(
                App.DatabaseService.GetConnectionString()
            );
            
            // Determine the tap type we're about to record
            var predictedTapType = await App.DatabaseService.GetLastTapTypeAsync(employee.EmployeeId);
            var nextTapTypePredicted = predictedTapType == "IN" ? "OUT" : "IN";
            
            var missedLogValidation = await missedLogValidator.ValidateMissedLogAsync(
                employee.EmployeeId,
                currentTime,
                nextTapTypePredicted
            );

            if (missedLogValidation.HasApprovedMissedLog && missedLogValidation.ShouldOverrideTime)
            {
                // ? APPROVED MISSED LOG - OVERRIDE TIME!
                Debug.WriteLine($"? MISSED LOG TIME OVERRIDE ACTIVE!");
                Debug.WriteLine($"   Original Time: {currentTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Override Time: {missedLogValidation.RequestedTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Request ID: {missedLogValidation.RequestId}");
                Debug.WriteLine($"   Reason: {missedLogValidation.Reason}");
                
                // ? CRITICAL: Validate that RequestedTime is not null before using it
                if (missedLogValidation.RequestedTime.HasValue)
                {
                    // ? CRITICAL: Override the currentTime variable with requested_time
                    currentTime = missedLogValidation.RequestedTime.Value;
                    
                    Debug.WriteLine($"");
                    Debug.WriteLine($"? TIME SUCCESSFULLY OVERRIDDEN!");
                    Debug.WriteLine($"   All subsequent calculations will use: {currentTime:yyyy-MM-dd HH:mm:ss}");
                    Debug.WriteLine($"   Status will be calculated based on this time");
                    
                    // Show notification to user
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                        
                        CustomMessageDialog.ShowInfo(
                            "Missed Log Request Applied",
                            $"Employee: {employee.FullName}\n" +
                            $"Request ID: {missedLogValidation.RequestId}\n\n" +
                            $"Reason: {ReasonTextFormatter.ToFormal(missedLogValidation.Reason)}\n\n" +
                            $"Recording time as: {currentTime:yyyy-MM-dd h:mm tt}\n" +
                            $"(Instead of actual tap time)\n\n" +
                            $"Status will be calculated based on this time.",
                            countdown: 5
                        );
                        
                        // Restore focus
                        if (rfidTextBox != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    rfidTextBox.Focus();
                                    System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                })
                            );
                        }
                    });
                }
                else
                {
                    // ? CRITICAL ERROR: requested_time is NULL in database!
                    Debug.WriteLine($"");
                    Debug.WriteLine($"? ERROR: Missed log request found but requested_time is NULL!");
                    Debug.WriteLine($"   Request ID: {missedLogValidation.RequestId}");
                    Debug.WriteLine($"   This request is invalid - ignoring override");
                    Debug.WriteLine($"   Using actual tap time instead: {currentTime:yyyy-MM-dd HH:mm:ss}");
                    
                    // Log this as a data integrity issue
                    await attemptLogger.LogErrorAsync(
                        rfidCode,
                        $"Missed log request #{missedLogValidation.RequestId} has NULL requested_time",
                        employee?.EmployeeId,
                        employee?.FullName
                    );
                    
                    // Show warning to user
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                        
                        CustomMessageDialog.ShowWarning(
                            "Invalid Missed Log Request",
                            $"Employee: {employee!.FullName}\n" +
                            $"Request ID: {missedLogValidation.RequestId}\n\n" +
                            $"The missed log request does not have a valid requested time.\n\n" +
                            $"Using actual tap time instead.\n\n" +
                            $"Please contact the administrator to fix this request.",
                            countdown: 5
                        );
                        
                        // Restore focus
                        if (rfidTextBox != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    rfidTextBox.Focus();
                                    System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                })
                            );
                        }
                    });
                }
            }
            else
            {
                Debug.WriteLine($"? No approved missed log - using actual tap time");
            }

            // ???????????????????????????????????????????????????????????????????
            // PRIORITY 3: SUBSTITUTION VALIDATION
            // ???????????????????????????????????????????????????????????????????
            Debug.WriteLine($"");
            Debug.WriteLine($"?? CHECKING CLASS SUBSTITUTIONS...");
            
            var substitutionValidator = new SubstitutionValidationService(
                App.DatabaseService.GetConnectionString()
            );
            
            var substitutionValidation = await substitutionValidator.ValidateSubstitutionStatusAsync(
                employee!.EmployeeId,
                currentTime
            );

            if (substitutionValidation.HasSubstitution)
            {
                Debug.WriteLine($"?? SUBSTITUTION DETECTED!");
                
                if (substitutionValidation.IsOriginalEmployee)
                {
                    // ???????????????????????????????????????????????????????????????????
                    // ?? CRITICAL CHECK: Is ALL schedule substituted? (Block attendance!)
                    // ???????????????????????????????????????????????????????????????????
                    Debug.WriteLine($"");
                    Debug.WriteLine($"?? Checking if ALL schedules are substituted...");
                    
                    var allSubstitutedHelper = new AllSchedulesSubstitutedHelper(
                        App.DatabaseService.GetConnectionString()
                    );
                    
                    var allSubstitutedWarning = await allSubstitutedHelper.CheckAllSchedulesSubstitutedAsync(
                        employee.EmployeeId,
                        currentTime
                    );
                    
                    if (allSubstitutedWarning != null && allSubstitutedWarning.ShouldMarkAbsent)
                    {
                        // ?? ALL SCHEDULES SUBSTITUTED - BLOCK AND SHOW WARNING!
                        await attemptLogger.LogBlockedAttemptAsync(
                            rfidCode,
                            employee.EmployeeId,
                            employee.FullName,
                            "ALL_SCHEDULES_SUBSTITUTED"
                        );
                        
                        Debug.WriteLine($"?? ALL SCHEDULES SUBSTITUTED - BLOCKING ATTENDANCE!");
                        Debug.WriteLine($"   Total Schedules: {allSubstitutedWarning.TotalScheduleCount}");
                        Debug.WriteLine($"   Substituted: {allSubstitutedWarning.SubstitutedCount}");
                        Debug.WriteLine($"   Substitute(s): {allSubstitutedWarning.SubstituteName}");
                        Debug.WriteLine($"   ?? Automatically marking employee as ABSENT");
                        
                        // Build warning message
                        var warningDetails = $"Employee: {employee.FullName}\n" +
                                           $"Date: {currentTime:dddd, MMMM dd, yyyy}\n\n" +
                                           $"Total Schedules: {allSubstitutedWarning.TotalScheduleCount}\n" +
                                           $"Substituted by: {allSubstitutedWarning.SubstituteName}\n" +
                                           $"Time: {allSubstitutedWarning.StartTime:hh\\:mm} - {allSubstitutedWarning.EndTime:hh\\:mm}\n\n" +
                                           $"?? All your classes/exams have been substituted.\n" +
                                           $"You are automatically marked as ABSENT for today.\n\n" +
                                           $"No attendance will be recorded.";
                        
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var mainWindow = Application.Current.MainWindow as MainWindow;
                            var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                            
                            // Show warning dialog
                            CustomMessageDialog.ShowWarning(
                                "All Schedules Substituted",
                                warningDetails,
                                countdown: 7
                            );
                            
                            // Restore focus
                            if (rfidTextBox != null)
                            {
                                Application.Current.Dispatcher.BeginInvoke(
                                    System.Windows.Threading.DispatcherPriority.Background,
                                    new Action(() =>
                                    {
                                        rfidTextBox.Focus();
                                        System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                    })
                                );
                            }
                        });
                        
                        await Task.Delay(100);
                        ResetToReadyState();
                        RfidInput = string.Empty;
                        
                        // Reset cooldown
                        await Task.Delay(2000);
                        _isInCooldown = false;
                        
                        return false;
                    }
                    
                    // ???????????????????????????????????????????????????????????????????
                    // If not all substituted, show normal substitution notice
                    // ???????????????????????????????????????????????????????????????????
                    // ?? ORIGINAL EMPLOYEE - Notify about substitution (ALLOW attendance)
                    Debug.WriteLine($"   Mode: Original Employee (Being Substituted)");
                    Debug.WriteLine($"   Substitute: {substitutionValidation.SubstituteName}");
                    Debug.WriteLine($"   Time: {substitutionValidation.StartTime:hh\\:mm} - {substitutionValidation.EndTime:hh\\:mm}");
                    
                    // Build notification message for original employee
                    var substitutionInfo = $"Class Substitution Notice\n\n" +
                                         $"Employee: {employee.FullName}\n" +
                                         $"Date: {currentTime:dddd, MMMM dd, yyyy}\n\n" +
                                         $"Your class has been substituted:\n\n";
                    
                    if (!string.IsNullOrEmpty(substitutionValidation.OriginalSubjectName))
                    {
                        substitutionInfo += $"Subject: {substitutionValidation.OriginalSubjectName}\n";
                        substitutionInfo += $"Section: {substitutionValidation.OriginalSection}\n";
                    }
                    
                    substitutionInfo += $"Time: {substitutionValidation.StartTime:hh\\:mm} - {substitutionValidation.EndTime:hh\\:mm}\n";
                    
                    if (!string.IsNullOrEmpty(substitutionValidation.OriginalRoom))
                    {
                        substitutionInfo += $"Room: {substitutionValidation.OriginalRoom}\n";
                    }
                    
                    substitutionInfo += $"\nSubstitute Teacher: {substitutionValidation.SubstituteName}\n";
                    substitutionInfo += $"Reason: {ReasonTextFormatter.ToFormal(substitutionValidation.SubstitutionReason)}\n\n";
                    substitutionInfo += $"Your attendance for other schedules\n";
                    substitutionInfo += $"   will proceed normally.";
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                        
                        // Show INFO dialog (not blocking)
                        CustomMessageDialog.ShowInfo(
                            "Class Substitution Notice",
                            substitutionInfo,
                            countdown: 5
                        );
                        
                        // Restore focus
                        if (rfidTextBox != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    rfidTextBox.Focus();
                                    System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                })
                            );
                        }
                    });
                    
                    // ? CONTINUE - Original employee can still attend other classes
                    // The substituted schedule will be filtered out in schedule validation
                }
                else if (substitutionValidation.IsSubstituteEmployee)
                {
                    // ?? SUBSTITUTE EMPLOYEE - Notify about substitution duty (ALLOW attendance)
                    Debug.WriteLine($"   Mode: Substitute Employee (Covering Class)");
                    Debug.WriteLine($"   Original: {substitutionValidation.OriginalEmployeeName}");
                    Debug.WriteLine($"   Time: {substitutionValidation.StartTime:hh\\:mm} - {substitutionValidation.EndTime:hh\\:mm}");
                    
                    // Build notification message for substitute employee
                    var substituteInfo = $"Substitution Duty\n\n" +
                                       $"Employee: {employee.FullName}\n" +
                                       $"Date: {currentTime:dddd, MMMM dd, yyyy}\n\n" +
                                       $"You are substituting for:\n" +
                                       $"{substitutionValidation.OriginalEmployeeName}\n\n";
                    
                    if (!string.IsNullOrEmpty(substitutionValidation.SubstituteSubjectName))
                    {
                        substituteInfo += $"Subject: {substitutionValidation.SubstituteSubjectName}\n";
                        substituteInfo += $"Section: {substitutionValidation.SubstituteSection}\n";
                    }
                    
                    substituteInfo += $"Time: {substitutionValidation.StartTime:hh\\:mm} - {substitutionValidation.EndTime:hh\\:mm}\n";
                    
                    if (!string.IsNullOrEmpty(substitutionValidation.SubstituteRoom))
                    {
                        substituteInfo += $"Room: {substitutionValidation.SubstituteRoom}\n";
                    }
                    
                    substituteInfo += $"\nReason: {ReasonTextFormatter.ToFormal(substitutionValidation.SubstitutionReason)}\n\n";
                    substituteInfo += $"Please time in/out during this class.\n";
                    substituteInfo += $"   Attendance will be recorded under\n";
                    substituteInfo += $"   your name.";
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        var rfidTextBox = mainWindow?.FindName("RfidTextBox") as System.Windows.Controls.TextBox;
                        
                        // Show INFO dialog (not blocking)
                        CustomMessageDialog.ShowInfo(
                            "Substitution Duty",
                            substituteInfo,
                            countdown: 5
                        );
                        
                        // Restore focus
                        if (rfidTextBox != null)
                        {
                            Application.Current.Dispatcher.BeginInvoke(
                                System.Windows.Threading.DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    rfidTextBox.Focus();
                                    System.Windows.Input.Keyboard.Focus(rfidTextBox);
                                })
                            );
                        }
                    });
                    
                    // ? CONTINUE - Substitute employee proceeds with this class
                }
            }
            else
            {
                Debug.WriteLine($"? No substitution found - Continue validation");
            }

            // ???????????????????????????????????????????????
            // PRIORITY 4: EMPLOYMENT STATUS VALIDATION
            // ???????????????????????????????????????????????
            Debug.WriteLine($"");
            Debug.WriteLine($"?? CHECKING EMPLOYMENT STATUS RULES...");
            
            var employmentValidator = new EmploymentStatusValidationService(
                App.DatabaseService.GetConnectionString()
            );
            
            // Use custom time for validation
            var validationResult = await employmentValidator.ValidateAttendanceRequestAsync(
                employee.EmployeeId,
                currentTime  // Use GetCurrentTime() instead of DateTime.Now
            );

            if (!validationResult.IsAllowed)
            {
                // ?? ATTENDANCE BLOCKED - Log the attempt
                await attemptLogger.LogBlockedAttemptAsync(
                    rfidCode,
                    employee.EmployeeId,
                    employee.FullName,
                    validationResult.Reason ?? "VALIDATION_FAILED"
                );
                
                Debug.WriteLine($"?? ATTENDANCE BLOCKED!");
                Debug.WriteLine($"   Status: {validationResult.Status}");
                Debug.WriteLine($"   Reason: {validationResult.Reason}");
                Debug.WriteLine($"   Message: {validationResult.Message}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Build detailed message with employee info
                    var detailedMessage = $"Employee: {employee.FullName}\n" +
                                        $"Status: {employee.EmploymentStatus ?? "Unknown"}\n" +
                                        $"Date: {currentTime:dddd, MMMM dd, yyyy}\n" +
                                        $"Day: {currentTime.DayOfWeek}\n\n" +
                                        $"Reason: {ReasonTextFormatter.ToFormal(validationResult.Reason ?? "VALIDATION_FAILED")}";
                    
                    // Show warning dialog with 3-second countdown
                    CustomMessageDialog.ShowWarning(
                        validationResult.Message,
                        detailedMessage,
                        countdown: 3
                    );
                });
                
                await Task.Delay(100); // Small delay to ensure dialog is shown
                ResetToReadyState();
                RfidInput = string.Empty;
                
                // Reset cooldown for blocked attempt
                await Task.Delay(2000);
                _isInCooldown = false;
                
                return false;
            }

            // ? ATTENDANCE ALLOWED
            Debug.WriteLine($"? ATTENDANCE ALLOWED!");
            Debug.WriteLine($"   Mode: {validationResult.Mode}");
            Debug.WriteLine($"   Reason: {validationResult.Reason}");
            Debug.WriteLine($"   Next Tap Type: {validationResult.NextTapType ?? "Determined by last tap"}");
            
            if (validationResult.ScheduleDetails != null)
            {
                Debug.WriteLine($"   Schedule Details:");
                if (!string.IsNullOrEmpty(validationResult.ScheduleDetails.SubjectName))
                {
                    Debug.WriteLine($"     Subject: {validationResult.ScheduleDetails.SubjectName}");
                    Debug.WriteLine($"     Section: {validationResult.ScheduleDetails.Section}");
                }
                Debug.WriteLine($"     Time: {validationResult.ScheduleDetails.TimeStart:hh\\:mm} - {validationResult.ScheduleDetails.TimeEnd:hh\\:mm}");
            }

            // Get last tap to determine next action - ? FIX: Use DATE-SPECIFIC check
            var lastTapType = await App.HybridDatabase.GetLastTapTypeForDateAsync(employee.EmployeeId, currentTime.Date);
            var nextTapType = lastTapType == "IN" ? "OUT" : "IN";

            // If validation specifies next tap type, use it
            if (!string.IsNullOrEmpty(validationResult.NextTapType))
            {
                nextTapType = validationResult.NextTapType;
            }

            Debug.WriteLine($"?? DATE-SPECIFIC TAP CHECK:");
            Debug.WriteLine($"   Date: {currentTime:yyyy-MM-dd}");
            Debug.WriteLine($"   Last Tap on THIS date: {lastTapType}");
            Debug.WriteLine($"   Next Tap: {nextTapType}");
            
            // ???????????????????????????????????????????????
            // ? CRITICAL: Check if TIME IN exists on THIS date before allowing TIME OUT
            // ???????????????????????????????????????????????
            var todayLogsCheck = preFetchedTodayLogs ?? await App.HybridDatabase.GetTodayAttendanceForDateAsync(employee.EmployeeId, currentTime.Date);
            var hasTimeInCheck = todayLogsCheck.Any(l => l.LogType == "IN");
            
            if (nextTapType == "OUT" && !hasTimeInCheck)
            {
                Debug.WriteLine("?? BLOCKED: Cannot TIME OUT without TIME IN on this date!");
                Debug.WriteLine($"   Date: {currentTime:yyyy-MM-dd}");
                Debug.WriteLine($"   No TIME IN found for this date");
                Debug.WriteLine($"   Forcing next tap to be TIME IN");
                
                nextTapType = "IN";  // Force TIME IN
            }

            // ??????????????????????????????????????????????????????????
            // ? CRITICAL: Check if attendance is ALREADY COMPLETE
            // ? This MUST happen BEFORE recording to prevent duplicates!
            // ??????????????????????????????????????????????????????????
            var hasTimeOutCheck = todayLogsCheck.Any(l => l.LogType == "OUT");
            var isAttendanceCompleteCheck = hasTimeInCheck && hasTimeOutCheck;

            if (isAttendanceCompleteCheck)
            {
                Debug.WriteLine("?? ATTENDANCE ALREADY COMPLETE - BLOCKED BEFORE RECORDING!");
                Debug.WriteLine($"   Employee has both TIME IN and TIME OUT for {currentTime:yyyy-MM-dd}");
                Debug.WriteLine("   ? PREVENTING DUPLICATE TAP");
                
                // Log blocked attempt (for record keeping)
                await attemptLogger.LogBlockedAttemptAsync(
                    rfidCode,
                    employee.EmployeeId,
                    employee.FullName,
                    "ATTENDANCE_COMPLETE"
                );
                
                // Track tap count
                var employeeKey = $"{employee.EmployeeId}_{currentTime:yyyy-MM-dd}";
                if (!_employeeTapCountToday.ContainsKey(employeeKey))
                {
                    _employeeTapCountToday[employeeKey] = 0;
                }
                _employeeTapCountToday[employeeKey]++;
                var tapCount = _employeeTapCountToday[employeeKey];
                
                // Get TIME IN and TIME OUT logs
                var timeInLog = todayLogsCheck.First(l => l.LogType == "IN");
                var timeOutLog = todayLogsCheck.First(l => l.LogType == "OUT");
                
                // Show Complete Attendance Window
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Debug.WriteLine("?? Opening Complete Attendance Window...");
                    
                    var completeWindow = new CompleteAttendanceWindow(
                        employee,
                        timeInLog,
                        timeOutLog,
                        tapCount
                    );
                    completeWindow.ShowDialog();
                    
                    Debug.WriteLine("? Complete Attendance window closed");
                });
                
                // Update recent employees
                await LoadRecentEmployeesAsync();
                
                // Reset to ready state
                ResetToReadyState();
                
                // Reset cooldown
                await Task.Delay(2000);
                _isInCooldown = false;

                // Track successful tap for employee cooldown
                _employeeLastTapTime[rfidCode] = DateTime.Now;

                return true; // Success but attendance complete - NO RECORDING
            }

            // ? ATTENDANCE NOT COMPLETE - Proceed with recording
            Debug.WriteLine("? Attendance not complete - proceeding with recording");

            // ???????????????????????????????????????????????
            // FIX: Override employment validation Mode if holiday has NULL status
            // This ensures online_class holidays with schedules get proper status calculation
            // ???????????????????????????????????????????????
            if (holidayValidation.IsHolidayWithAttendance && 
                holidayValidation.AttendanceStatus == null &&
                holidayValidation.AttendanceMode == "ONLINE_CLASS_WITH_SCHEDULE")
            {
                Debug.WriteLine($"");
                Debug.WriteLine($"? ONLINE_CLASS FIX APPLIED:");
                Debug.WriteLine($"   Holiday returned NULL status - employee has schedule");
                Debug.WriteLine($"   Overriding employment Mode to allow normal calculation");
                Debug.WriteLine($"   Original Mode: {validationResult.Mode}");
                
                // Clear the Mode so it doesn't force admin-time
                validationResult.Mode = null;
                
                Debug.WriteLine($"   New Mode: NULL (allows normal status calculation)");
            }

            // Ensure substituted employees are recorded against substitution schedule,
            // not default work schedule (e.g., 7-5).
            if (substitutionValidation.HasSubstitution &&
                substitutionValidation.IsSubstituteEmployee &&
                substitutionValidation.StartTime.HasValue &&
                substitutionValidation.EndTime.HasValue)
            {
                validationResult.Mode = "ClassBased";
                validationResult.Reason = "SUBSTITUTION_SCHEDULE";
                validationResult.ScheduleDetails = new RAMSOfficial.Services.ScheduleInfo
                {
                    ScheduleId = substitutionValidation.SubstituteScheduleId,
                    SubjectName = substitutionValidation.SubstituteSubjectName ?? "Substituted Class",
                    Section = substitutionValidation.SubstituteSection,
                    TimeStart = substitutionValidation.StartTime.Value,
                    TimeEnd = substitutionValidation.EndTime.Value,
                    Room = substitutionValidation.SubstituteRoom,
                    DayOfWeek = currentTime.DayOfWeek.ToString()
                };

                Debug.WriteLine($"✅ Substitution schedule injected for attendance save:");
                Debug.WriteLine($"   ScheduleId: {validationResult.ScheduleDetails.ScheduleId?.ToString() ?? "(null)"}");
                Debug.WriteLine($"   Time: {validationResult.ScheduleDetails.TimeStart:hh\\:mm} - {validationResult.ScheduleDetails.TimeEnd:hh\\:mm}");
                Debug.WriteLine($"   Subject: {validationResult.ScheduleDetails.SubjectName}");
            }

            // Record attendance - pass employment validation result to preserve Mode
            // ? CRITICAL: Also pass missed log validation to ensure requested time is used
            var attendanceLog = await App.HybridDatabase.RecordAttendanceAsync(
                employee.EmployeeId, 
                nextTapType, 
                validationResult,
                missedLogValidation  // ? Pass missed log validation here
            );

            // ? SUCCESS - Log the successful attempt
            await attemptLogger.LogSuccessAsync(rfidCode, employee.EmployeeId, employee.FullName);

            Debug.WriteLine($"? Attendance Recorded!");
            Debug.WriteLine($"   Type: {attendanceLog.LogType}");
            Debug.WriteLine($"   Time: {attendanceLog.LogTime:yyyy-MM-dd HH:mm:ss}");
            Debug.WriteLine($"   Time Mode: {(TimeChangerWindow.IsTimeOverridden() ? "CUSTOM OVERRIDE" : "Real Time")}");
            
            // Record tap in state manager
            CustomTimeStateManager.RecordTap(employee.EmployeeId, nextTapType, attendanceLog.LogTime);
            
            // Add validation mode to notes if AdminTime
            if (validationResult.Mode == "AdminTime")
            {
                Debug.WriteLine($"   Mode: Admin Time (PT Full Load - No class schedule)");
            }

            // Update UI
            CurrentEmployee = employee;
            LastTapType = nextTapType;
            LastTapTime = attendanceLog.LogTime;

            // Load today's logs
            var todayLogs = await App.HybridDatabase.GetTodayAttendanceAsync(employee.EmployeeId);
            TodayLogs.Clear();
            foreach (var log in todayLogs)
            {
                TodayLogs.Add(log);
            }

            Debug.WriteLine($"?? Loaded {todayLogs.Count()} log(s) for today");

            // Track tap count for this employee today
            var employeeKeyForCount = $"{employee.EmployeeId}_{currentTime:yyyy-MM-dd}";
            if (!_employeeTapCountToday.ContainsKey(employeeKeyForCount))
            {
                _employeeTapCountToday[employeeKeyForCount] = 0;
            }
            _employeeTapCountToday[employeeKeyForCount]++;
            var tapCountFinal = _employeeTapCountToday[employeeKeyForCount];

            Debug.WriteLine($"?? Tap count for today: {tapCountFinal}");

            // ???????????????????????????????????????????????????????????????????
            // ?? CRITICAL FIX: Get schedule times based on employee role
            // - If SUBSTITUTE employee (Rohan): Get substitution times (08:00-11:00)
            // - If ORIGINAL employee (Brian): Get REMAINING schedule AFTER excluding substituted class
            // ???????????????????????????????????????????????????????????????????
            TimeSpan? expectedTimeIn = null;
            TimeSpan? expectedTimeOut = null;
            
            // CASE 1: Check if this employee is SUBSTITUTING someone
            var substitutionHelper = new SubstitutionScheduleHelper(App.DatabaseService.GetConnectionString());
            var substitutionTimes = await substitutionHelper.GetSubstitutionTimesAsync(employee.EmployeeId, currentTime.Date);
            
            if (substitutionTimes != null)
            {
                // ? SUBSTITUTE EMPLOYEE - Use substitution times
                expectedTimeIn = substitutionTimes.StartTime;
                expectedTimeOut = substitutionTimes.EndTime;
                
                Debug.WriteLine($"?? SUBSTITUTE EMPLOYEE - SUBSTITUTION TIMES RETRIEVED:");
                Debug.WriteLine($"   Employee: {employee.FullName} (ID: {employee.EmployeeId})");
                Debug.WriteLine($"   Substituting for: {substitutionTimes.OriginalEmployeeName}");
                Debug.WriteLine($"   TIME IN: {expectedTimeIn:hh\\:mm}");
                Debug.WriteLine($"   TIME OUT: {expectedTimeOut:hh\\:mm}");
                Debug.WriteLine($"   Will be displayed in UI!");
            }
            else
            {
                // CASE 2: Check if this employee IS THE ORIGINAL employee being substituted
                var remainingScheduleHelper = new OriginalEmployeeRemainingScheduleHelper(App.DatabaseService.GetConnectionString());
                var remainingSchedule = await remainingScheduleHelper.GetRemainingScheduleAsync(employee.EmployeeId, currentTime.Date);
                
                if (remainingSchedule != null && remainingSchedule.HasRemainingSchedule)
                {
                    // ? ORIGINAL EMPLOYEE - Use REMAINING schedule (excludes substituted class)
                    expectedTimeIn = remainingSchedule.EarliestStartTime;
                    expectedTimeOut = remainingSchedule.LatestEndTime;
                    
                    Debug.WriteLine($"?? ORIGINAL EMPLOYEE - REMAINING SCHEDULE RETRIEVED:");
                    Debug.WriteLine($"   Employee: {employee.FullName} (ID: {employee.EmployeeId})");
                    Debug.WriteLine($"   Total Classes: {remainingSchedule.TotalClassCount}");
                    Debug.WriteLine($"   Substituted Classes: {remainingSchedule.SubstitutedClassCount}");
                    Debug.WriteLine($"   Remaining Classes: {remainingSchedule.RemainingClassCount}");
                    Debug.WriteLine($"   TIME IN: {expectedTimeIn:hh\\:mm} (earliest remaining)");
                    Debug.WriteLine($"   TIME OUT: {expectedTimeOut:hh\\:mm} (latest remaining)");
                    Debug.WriteLine($"   Will be displayed in UI!");
                }
                else if (remainingSchedule != null && !remainingSchedule.HasRemainingSchedule)
                {
                    Debug.WriteLine($"?? ORIGINAL EMPLOYEE - ALL CLASSES SUBSTITUTED:");
                    Debug.WriteLine($"   Employee: {employee.FullName} (ID: {employee.EmployeeId})");
                    Debug.WriteLine($"   Total Classes: {remainingSchedule.TotalClassCount}");
                    Debug.WriteLine($"   All {remainingSchedule.SubstitutedClassCount} class(es) are substituted");
                    Debug.WriteLine($"   NO remaining schedule to display");
                }
                else
                {
                    Debug.WriteLine($"?? No substitution involvement for this employee");
                }
            }

            // ???????????????????????????????????????????????????????????????????
            // ?? FINAL VALIDATION: Create comprehensive validation result
            // This includes holiday information needed for warning dialog
            // ???????????????????????????????????????????????????????????????????
            
            // Get comprehensive validation from AttendanceValidationService
            var attendanceValidationService = new AttendanceValidationService(App.DatabaseService.GetConnectionString());
            var comprehensiveValidation = await attendanceValidationService.ValidateAttendanceAsync(
                employee.EmployeeId, 
                currentTime, 
                nextTapType
            );

            Debug.WriteLine($"?? Comprehensive validation completed");
            Debug.WriteLine($"   Status: {comprehensiveValidation.AttendanceStatus}");
            Debug.WriteLine($"   Priority: {comprehensiveValidation.Priority}");
            Debug.WriteLine($"   Schedule Type: {comprehensiveValidation.ScheduleType}");
            Debug.WriteLine($"   Is Holiday: {comprehensiveValidation.IsHoliday}");
            Debug.WriteLine($"   Holiday Type: {comprehensiveValidation.HolidayType}");
            Debug.WriteLine($"   Schedule Details: {comprehensiveValidation.ScheduleDetails}");
            
            // Override with database status and add missing fields
            comprehensiveValidation.AttendanceStatus = attendanceLog.AttendanceStatus ?? string.Empty; // Use actual database status
            comprehensiveValidation.IsLate = attendanceLog.IsLate ?? false;
            comprehensiveValidation.IsEarlyOut = attendanceLog.IsEarlyOut ?? false;
            comprehensiveValidation.LateMinutes = attendanceLog.LateMinutes;
            comprehensiveValidation.UndertimeMinutes = attendanceLog.UndertimeMinutes;
            
            // Add schedule times if we found them
            if (expectedTimeIn.HasValue)
                comprehensiveValidation.ExpectedTimeIn = expectedTimeIn;
            if (expectedTimeOut.HasValue)
                comprehensiveValidation.ExpectedTimeOut = expectedTimeOut;
            if (substitutionTimes != null)
                comprehensiveValidation.IsSubstitution = true;

            Debug.WriteLine($"?? Final validation ready for UI:");
            Debug.WriteLine($"   Status: {comprehensiveValidation.AttendanceStatus}");
            Debug.WriteLine($"   Is Holiday: {comprehensiveValidation.IsHoliday}");
            if (comprehensiveValidation.IsHoliday)
            {
                Debug.WriteLine($"   Holiday Type: {comprehensiveValidation.HolidayType}");
                Debug.WriteLine($"   Schedule Details: {comprehensiveValidation.ScheduleDetails}");
                Debug.WriteLine($"   ?? HOLIDAY WARNING DIALOG WILL BE TRIGGERED");
            }
            if (comprehensiveValidation.ExpectedTimeIn.HasValue && comprehensiveValidation.ExpectedTimeOut.HasValue)
                Debug.WriteLine($"   ? Expected Schedule: {comprehensiveValidation.ExpectedTimeIn:hh\\:mm} - {comprehensiveValidation.ExpectedTimeOut:hh\\:mm}");
            if (comprehensiveValidation.IsLate)
                Debug.WriteLine($"   Late by: {comprehensiveValidation.LateMinutes} minutes");
            if (comprehensiveValidation.IsEarlyOut)
                Debug.WriteLine($"   Undertime: {comprehensiveValidation.UndertimeMinutes} minutes");

            // Show employee profile window
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                Debug.WriteLine("?? Opening Employee Profile Window...");
                
                // ?? NEW: Show reporting day dialog FIRST if it's a reporting day
                if (comprehensiveValidation.IsHoliday && 
                    comprehensiveValidation.HolidayType?.ToLowerInvariant() == "suspended_asynchronous")
                {
                    Debug.WriteLine("?? REPORTING DAY DETECTED - SHOWING WARNING DIALOG FIRST");
                    
                    // Get the main window to show the dialog
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        // ?? STEP 1: Show simple blue information dialog first
                        await mainWindow.ShowReportingDayWarningAsync(
                            comprehensiveValidation.ScheduleDetails ?? "Reporting Day",
                            currentTime
                        );
                        
                        Debug.WriteLine("?? Warning dialog completed");
                        
                        // ?? STEP 2: Then show detailed information dialog with 3-second countdown
                        await mainWindow.ShowReportingDayInfoDialogAsync(
                            comprehensiveValidation.ScheduleDetails ?? "Reporting Day",
                            employee.FullName,
                            employee.StaffType ?? "Unknown",
                            employee.EmploymentStatus ?? "Unknown"
                        );
                    }
                    
                    Debug.WriteLine("?? All reporting day dialogs completed - now opening profile window");
                }
                
                // ?? NEW: Show online class dialog if it's an online class day
                if (comprehensiveValidation.IsHoliday && 
                    comprehensiveValidation.HolidayType?.ToLowerInvariant() == "online_class")
                {
                    Debug.WriteLine("?? ONLINE CLASS DETECTED - SHOWING INFORMATION DIALOG");
                    
                    // Get the main window to show the dialog
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        // Show online class information dialog with 3-second countdown
                        await mainWindow.ShowOnlineClassInfoDialogAsync(
                            comprehensiveValidation.ScheduleDetails ?? "Online Class",
                            currentTime
                        );
                    }
                    
                    Debug.WriteLine("?? Online class dialog completed - now opening profile window");
                }
                
                var profileWindow = new EmployeeProfileWindow(
                    employee, 
                    nextTapType, 
                    attendanceLog.LogTime, 
                    todayLogs.ToList(),
                    comprehensiveValidation, // ?? USE COMPREHENSIVE VALIDATION (includes holiday info)
                    false); // Not complete
                profileWindow.ShowDialog();
                
                Debug.WriteLine("?? Profile window closed");
                
                // Show notification after profile window closes
                if (Application.Current.MainWindow != null)
                {
                    NotificationService.ShowAttendanceNotification(
                        Application.Current.MainWindow,
                        comprehensiveValidation,
                        employee.FullName);
                }
            });

            // Update recent employees
            await LoadRecentEmployeesAsync();
            
            // ???????????????????????????????????????????????
            // AUTO-CLEAR TIME OVERRIDE after successful tap
            // ???????????????????????????????????????????????
            if (TimeChangerWindow.IsTimeOverridden())
            {
                Debug.WriteLine("");
                Debug.WriteLine("?? AUTO-CLEARING TIME OVERRIDE");
                Debug.WriteLine("   Attendance successfully recorded with custom time");
                Debug.WriteLine("   Returning to real-time mode...");
                
                TimeChangerWindow.ClearCustomTime();
                
                Debug.WriteLine("   ? Time override cleared - back to real time");
            }
            
            // Reset to ready state
            ResetToReadyState();

            // Reset cooldown after successful processing
            await Task.Delay(TAP_COOLDOWN_SECONDS * 1000);
            _isInCooldown = false;

            // Track successful tap for employee cooldown
            if (!isTimeOverrideActive)
            {
                _employeeLastTapTime[rfidCode] = DateTime.Now;
            }

            Debug.WriteLine("???????????????????????????????????????????");
            Debug.WriteLine("? RFID PROCESSING COMPLETE");
            Debug.WriteLine("???????????????????????????????????????????");
            
            return true;
        }
        catch (Exception ex)
        {
            // ? ERROR - Log the error attempt
            await attemptLogger.LogErrorAsync(
                rfidCode,
                ex.Message,
                employee?.EmployeeId,
                employee?.FullName
            );
            
            StatusMessage = $"? Error: {ex.Message}";
            StatusColor = "#F44336";

            Debug.WriteLine($"? ERROR processing RFID:");
            Debug.WriteLine($"   {ex.Message}");
            Debug.WriteLine(ex.StackTrace);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                var rfidTextBox = mainWindow?.FindName("RFIDTextBox") as System.Windows.Controls.TextBox;

                CustomMessageDialog.ShowError(
                    "An error occurred while processing attendance",
                    $"{ex.Message}\n\n" +
                    "This attempt has been logged.\n\n" +
                    "Please try again or contact support if the problem persists.",
                    countdown: 3
                );

                // Restore focus
                if (rfidTextBox != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() =>
                        {
                            rfidTextBox.Focus();
                            System.Windows.Input.Keyboard.Focus(rfidTextBox);
                        })
                    );
                }
            });
            
            await Task.Delay(100);
            ResetToReadyState();
            RfidInput = string.Empty;
            
            // Reset cooldown for error
            await Task.Delay(2000);
            _isInCooldown = false;
            
            return false;
        }
    }

    private void ResetToReadyState()
    {
        IsProcessing = false;
        RfidInput = string.Empty;
        StatusMessage = IsManualInputMode ? "Ready - Enter RFID manually" : "Ready - Tap your RFID card";
        StatusColor = "#0066CC";
        LastTapType = string.Empty;
    }

    // DispatcherTimer tick handler for countdown
    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        if (Countdown <= 0)
        {
            Countdown = 0;
            _countdownTimer?.Stop();
            return;
        }

        Countdown--;
    }

    private async Task SimulateTapAsync()
    {
        // This method is for testing only - remove in production
        // Show manual input dialog instead
        await ToggleManualInputAsync();
    }
}

public class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter) => await _execute();
}
