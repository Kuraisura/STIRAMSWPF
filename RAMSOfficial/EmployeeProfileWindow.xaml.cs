using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RAMSOfficial.Models;
using RAMSOfficial.Services;
using RAMSOfficial.Helpers;

namespace RAMSOfficial;

public partial class EmployeeProfileWindow : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private int _countdown = 3;
    private readonly Employee _employee;
    private readonly string _tapType;
    private readonly DateTime _tapTime;
    private readonly List<AttendanceLog> _todayLogs;
    private readonly AttendanceValidationResult? _validationResult;
    private readonly bool _isAttendanceComplete;

    public EmployeeProfileWindow(
        Employee employee, 
        string tapType, 
        DateTime tapTime, 
        List<AttendanceLog> todayLogs,
        AttendanceValidationResult? validationResult = null,
        bool isAttendanceComplete = false)
    {
        InitializeComponent();
        
        _employee = employee;
        _tapType = tapType;
        _tapTime = tapTime;
        _todayLogs = todayLogs;
        _validationResult = validationResult;
        _isAttendanceComplete = isAttendanceComplete;

        // Setup countdown timer
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
        
        // Connection status indicator is NOT shown on this auto-closing popup
        // to avoid blocking the header text. It remains on MainWindow only.

        // Holiday warning dialog shows BEFORE profile window via
        // MainWindow.ShowReportingDayInfoDialogAsync() — not duplicated here.
    }
    
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadEmployeeDataAsync();
        StartCountdown();
    }

    private async Task LoadEmployeeDataAsync()
    {
        // Check if attendance is complete (both IN and OUT)
        if (_isAttendanceComplete)
        {
            // Show "Attendance Complete" status
            StatusTextBlock.Text = "ATTENDANCE COMPLETE FOR TODAY";
            StatusTextBlock.Foreground = AttendanceStatusColors.OnTime;

            // Show informative message
            TapTypeTextBlock.Text = "NO FURTHER TAPS NEEDED";
            TapTypeTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800")!);
        }
        else
        {
            // Normal status based on tap type
            var statusPrefix = _tapType == "IN" ? "TIME IN" : "TIME OUT";

            StatusTextBlock.Text = $"{statusPrefix} RECORDED";
            StatusTextBlock.Foreground = GetStatusColor();

            // Show tap type label
            TapTypeTextBlock.Text = statusPrefix;
            TapTypeTextBlock.Foreground = _tapType == "IN" ? 
                AttendanceStatusColors.OnTime : 
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1565C0")!);
        }

        // Avatar
        await LoadEmployeePhotoAsync();
        InitialTextBlock.Text = _employee.Initial;

        // Name and ID
        NameTextBlock.Text = _employee.FullName;
        SchoolIdRun.Text = _employee.SchoolId ?? "N/A";
        
        // Display StaffType below the name (Teaching or Non-Teaching)
        EmployeeTypeRun.Text = _employee.StaffType ?? _employee.EmployeeType ?? "Staff";

        // Details
        DepartmentTextBlock.Text = _employee.Department ?? "N/A";
        
        RoleRun.Text = _employee.Role ?? _employee.StaffType ?? "Employee";
        EmploymentStatusRun.Text = _employee.EmploymentStatus ?? "Active";

        // Tap Time - Show Expected Schedule Time
        await DisplayActualTimeAsync();
        DateTextBlock.Text = _tapTime.ToString("dddd, MMMM dd, yyyy");

        // ???????????????????????????????????????????????????????????????????
        // NEW APPROACH: Use DynamicScheduleUpdater to get COMBINED schedule
        // This includes: Original classes + Substitute duties
        // Example: Brian's 8-11 AM class + Mark's 12-3 PM substitute = 8:00 AM - 3:00 PM
        // ???????????????????????????????????????????????????????????????????
        
        bool scheduleDisplayed = false;
        
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"\uE946 ATTEMPTING TO GET COMBINED SCHEDULE");  // Info
        Debug.WriteLine($"   Employee: {_employee.FullName} (ID: {_employee.EmployeeId})");
        Debug.WriteLine($"   Date: {_tapTime.Date:yyyy-MM-dd}");
        Debug.WriteLine($"???????????????????????????????????????????");
        
        try
        {
            var dynamicUpdater = new DynamicScheduleUpdater(App.DatabaseService.GetConnectionString());
            var currentSchedule = await dynamicUpdater.GetCurrentExpectedScheduleAsync(_employee.EmployeeId, _tapTime.Date);

            Debug.WriteLine($"\uE946 DynamicScheduleUpdater returned:");  // Info
            Debug.WriteLine($"   HasSchedule: {currentSchedule.HasSchedule}");
            Debug.WriteLine($"   ExpectedTimeIn: {currentSchedule.ExpectedTimeIn}");
            Debug.WriteLine($"   ExpectedTimeOut: {currentSchedule.ExpectedTimeOut}");
            Debug.WriteLine($"   Total Schedules: {currentSchedule.TotalScheduleCount}");
            Debug.WriteLine($"   Original Classes: {currentSchedule.OriginalSchedules.Count}");
            Debug.WriteLine($"   Substitute Duties: {currentSchedule.SubstituteDuties.Count}");
            Debug.WriteLine($"   ErrorMessage: {currentSchedule.ErrorMessage ?? "(none)"}");

            if (currentSchedule.HasSchedule && currentSchedule.ExpectedTimeIn.HasValue && currentSchedule.ExpectedTimeOut.HasValue)
            {
                // Display COMBINED schedule (own classes + substitute duties)
                var scheduleDate = DateTime.Today;
                var timeInDateTime = scheduleDate.Add(currentSchedule.ExpectedTimeIn.Value);
                var timeOutDateTime = scheduleDate.Add(currentSchedule.ExpectedTimeOut.Value);
                
                // Show time grid, hide no classes message
                ScheduleTimeGrid.Visibility = Visibility.Visible;
                NoClassesTextBlock.Visibility = Visibility.Collapsed;
                
                TimeInTextBlock.Text = timeInDateTime.ToString("hh:mm tt");
                TimeOutTextBlock.Text = timeOutDateTime.ToString("hh:mm tt");

                Debug.WriteLine($"\uE73E Schedule Displayed from DynamicScheduleUpdater:");  // CheckMark
                Debug.WriteLine($"   Expected Time IN: {timeInDateTime:hh:mm tt}");
                Debug.WriteLine($"   Expected Time OUT: {timeOutDateTime:hh:mm tt}");
                Debug.WriteLine($"   Total Schedules: {currentSchedule.TotalScheduleCount}");
                Debug.WriteLine($"   Original Classes: {currentSchedule.OriginalSchedules.Count}");
                Debug.WriteLine($"   Substitute Duties: {currentSchedule.SubstituteDuties.Count}");
                
                if (currentSchedule.HasSubstituteDuties)
                {
                    Debug.WriteLine($"   \uE946 INCLUDES SUBSTITUTE DUTIES:");  // Info icon
                    foreach (var duty in currentSchedule.SubstituteDuties)
                    {
                        Debug.WriteLine($"      • {duty.StartTime:hh\\:mm}-{duty.EndTime:hh\\:mm} (for {duty.OriginalEmployeeName})");
                    }
                }
                
                if (currentSchedule.OriginalSchedules.Any())
                {
                    Debug.WriteLine($"   \uE946 INCLUDES ORIGINAL CLASSES:");  // Info icon
                    foreach (var cls in currentSchedule.OriginalSchedules)
                    {
                        Debug.WriteLine($"      • {cls.StartTime:hh\\:mm}-{cls.EndTime:hh\\:mm} {cls.SubjectName}");
                    }
                }
                
                scheduleDisplayed = true;
            }
            else
            {
                Debug.WriteLine($"\uE7BA DynamicScheduleUpdater returned but no valid schedule:");  // Warning
                Debug.WriteLine($"   HasSchedule: {currentSchedule.HasSchedule}");
                Debug.WriteLine($"   Expected schedule is NULL or missing values");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"\uE783 Error getting combined schedule:");  // Error
            Debug.WriteLine($"   Exception: {ex.Message}");
            Debug.WriteLine($"   StackTrace: {ex.StackTrace}");
        }

        if (!scheduleDisplayed && _validationResult?.ExpectedTimeIn.HasValue == true && _validationResult.ExpectedTimeOut.HasValue == true)
        {
            var scheduleDate = DateTime.Today;
            var timeInDateTime = scheduleDate.Add(_validationResult.ExpectedTimeIn.Value);
            var timeOutDateTime = scheduleDate.Add(_validationResult.ExpectedTimeOut.Value);

            ScheduleTimeGrid.Visibility = Visibility.Visible;
            NoClassesTextBlock.Visibility = Visibility.Collapsed;

            TimeInTextBlock.Text = timeInDateTime.ToString("hh:mm tt");
            TimeOutTextBlock.Text = timeOutDateTime.ToString("hh:mm tt");

            Debug.WriteLine($"\uE712 Schedule Displayed from validation result:");
            Debug.WriteLine($"   Schedule Type: {_validationResult.ScheduleType}");
            Debug.WriteLine($"   Expected Time IN: {timeInDateTime:hh:mm tt}");
            Debug.WriteLine($"   Expected Time OUT: {timeOutDateTime:hh:mm tt}");

            scheduleDisplayed = true;
        }
        
        Debug.WriteLine($"");
        Debug.WriteLine($"\uE946 Schedule Displayed: {scheduleDisplayed}");
        Debug.WriteLine($"");
        
        // FALLBACK: If DynamicScheduleUpdater didn't return a schedule
        if (!scheduleDisplayed)
        {
            Debug.WriteLine($"? FALLBACK: DynamicScheduleUpdater didn't return a schedule");  // Warning
            
            // Check if employee type requires default schedule display
            bool isNonTeaching = _employee.StaffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
            bool isRegularOrProvisionary = _employee.EmploymentStatus?.Contains("Regular", StringComparison.OrdinalIgnoreCase) == true ||
                                          _employee.EmploymentStatus?.Contains("Provisionary", StringComparison.OrdinalIgnoreCase) == true;
            
            bool shouldShowDefaultSchedule = isNonTeaching && isRegularOrProvisionary;
            
            Debug.WriteLine($"   isNonTeaching: {isNonTeaching}");
            Debug.WriteLine($"   isRegularOrProvisionary: {isRegularOrProvisionary}");
            Debug.WriteLine($"   shouldShowDefaultSchedule: {shouldShowDefaultSchedule}");
            
            if (shouldShowDefaultSchedule)
            {
                // Non-Teaching Regular/Provisionary - Show employee default schedule
                var scheduleDate = DateTime.Today;
                var timeInDateTime = scheduleDate.Add(_employee.ScheduleTimeIn);
                var timeOutDateTime = scheduleDate.Add(_employee.ScheduleTimeOut);
                
                ScheduleTimeGrid.Visibility = Visibility.Visible;
                NoClassesTextBlock.Visibility = Visibility.Collapsed;
                
                TimeInTextBlock.Text = timeInDateTime.ToString("hh:mm tt");
                TimeOutTextBlock.Text = timeOutDateTime.ToString("hh:mm tt");

                Debug.WriteLine($"? Schedule Displayed:");  // CheckMark
                Debug.WriteLine($"   Source: Employee default (Non-Teaching Regular/Provisionary)");
                Debug.WriteLine($"   Time In: {timeInDateTime:hh:mm tt}");
                Debug.WriteLine($"   Time Out: {timeOutDateTime:hh:mm tt}");
            }
            else
            {
                // Part-Time or Part-Time Full Load Teaching Staff - NO schedule display
                ScheduleTimeGrid.Visibility = Visibility.Collapsed;
                NoClassesTextBlock.Visibility = Visibility.Visible;

                Debug.WriteLine($"? No Classes Message Displayed:");  // Warning
                Debug.WriteLine($"   Source: No schedule (Part-Time/PTFL Teaching)");
                Debug.WriteLine($"   Staff Type: {_employee.StaffType}");
                Debug.WriteLine($"   Employment Status: {_employee.EmploymentStatus}");
                Debug.WriteLine($"   ? THIS IS WHY 'No Classes Scheduled' IS SHOWING!");  // Error
            }
        }

        if (_validationResult != null)
        {
            var sourceText = BuildScheduleSourceText(_validationResult);
            ApplyScheduleSourceBadge(sourceText);

            // Keep legacy note text hidden since schedule source is now shown
            // in a dedicated chip beside the status badge.
            StatusNotesTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApplyScheduleSourceBadge(null);
            StatusNotesTextBlock.Visibility = Visibility.Collapsed;
        }

        // Show attendance status BELOW Time In/Out or No Classes message
        if (_validationResult != null && !_isAttendanceComplete)
        {
            await ShowAttendanceStatusAsync(_validationResult);
        }
    }

    private void ApplyScheduleSourceBadge(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            ScheduleSourceBadgeBorder.Visibility = Visibility.Collapsed;
            ScheduleSourceBadgeTextBlock.Text = string.Empty;
            return;
        }

        ScheduleSourceBadgeTextBlock.Text = sourceText;
        ScheduleSourceBadgeBorder.Visibility = Visibility.Visible;
    }

    private static string BuildScheduleSourceText(AttendanceValidationResult validation)
    {
        var scheduleTypeLabel = validation.ScheduleType switch
        {
            "exam" => "Exam Schedule",
            "class" => "Class Schedule",
            "substitution" => "Substitute Schedule",
            "combined_schedule" => "Combined Schedule",
            "holiday" => "Holiday Rule",
            "verification" => "Verification Rule",
            "verification_readjusted" => "Verification Schedule",
            "verification_leave" => "Verification Leave",
            "regular" => "Regular Schedule",
            "admin_time" => "Admin Time",
            "admin-time" => "Admin Time",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(scheduleTypeLabel))
            return string.Empty;

        return string.IsNullOrWhiteSpace(validation.ScheduleDetails)
            ? $"Source: {scheduleTypeLabel}"
            : $"Source: {scheduleTypeLabel} • {validation.ScheduleDetails}";
    }

    private string GetStatusText()
    {
        if (_validationResult == null) return "ON TIME";

        return _validationResult.AttendanceStatus.ToUpper() switch
        {
            "ON-TIME" => "ON TIME",
            "ADMIN-TIME" => "ADMIN TIME",
            "LATE" => $"LATE ({((_validationResult.LateMinutes ?? 0) / 60.0):0.00} HOURS)",
            "UNDERTIME" => $"UNDERTIME ({((_validationResult.UndertimeMinutes ?? 0) / 60.0):0.00} HOURS)",
            "ABSENT" => "ABSENT",
            _ => "ON TIME"
        };
    }

    private SolidColorBrush GetStatusColor()
    {
        if (_validationResult == null || _isAttendanceComplete)
            return AttendanceStatusColors.OnTime;

        var status = _validationResult.AttendanceStatus;
        return AttendanceStatusColors.GetColorByStatus(status);
    }

    private async Task ShowAttendanceStatusAsync(AttendanceValidationResult validation)
    {
        // CRITICAL: Check for missed log request first!
        // If there's an approved missed log, ALWAYS show "ON TIME"
        var missedLogValidator = new MissedLogValidationService(App.DatabaseService.GetConnectionString());
        var missedLogValidation = await missedLogValidator.ValidateMissedLogAsync(
            _employee.EmployeeId,
            _tapTime,
            _tapType
        );
        
        string statusText;
        string statusColor;
        string backgroundColor;
        string borderColor;
        
        // OVERRIDE: If missed log exists, force ON TIME status
        if (missedLogValidation.HasApprovedMissedLog)
        {
            Debug.WriteLine($"? MISSED LOG DETECTED - FORCING STATUS TO 'ON TIME'");
            Debug.WriteLine($"   Request ID: {missedLogValidation.RequestId}");
            Debug.WriteLine($"   Reason: {missedLogValidation.Reason}");
            Debug.WriteLine($"   Original status from validation: {validation.AttendanceStatus}");
            Debug.WriteLine($"   Overriding to: ON TIME");
            
            // Force ON TIME display
            statusText = $"ON TIME (Missed Log #{missedLogValidation.RequestId})";
            statusColor = "#2E7D32"; // Dark Green
            backgroundColor = "#E8F5E9"; // Light Green
            borderColor = "#4CAF50"; // Green
        }
        else
        {
            // Normal status display (no missed log)
            var status = (validation.AttendanceStatus ?? "on-time")
                .Trim()
                .ToLowerInvariant()
                .Replace("_", "-")
                .Replace(" ", "-");
            
            // CRITICAL FIX: Check if admin-time is from holiday exemption
            // If Non-Teaching staff during online_class or suspended_asynchronous,
            // they should show actual status (on-time/late/undertime), NOT admin-time
            if (status == "admin-time")
            {
                var isNonTeachingStaff = _employee.StaffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
                
                // Check if there's a teaching-related holiday today
                var holidayValidator = new HolidayValidationService(App.DatabaseService.GetConnectionString());
                var holidayCheck = await holidayValidator.ValidateHolidayAttendanceAsync(
                    _employee.EmployeeId,
                    _employee.EmploymentStatus,
                    _employee.EmploymentType,
                    _employee.StaffType,
                    _tapTime
                );
                
                var isTeachingHoliday = holidayCheck.IsHolidayWithAttendance &&
                                       (holidayCheck.HolidayType?.ToLower() == "online_class" ||
                                        holidayCheck.HolidayType?.ToLower() == "online class" ||
                                        holidayCheck.HolidayType?.ToLower() == "suspended_asynchronous" ||
                                        holidayCheck.HolidayType?.ToLower() == "suspended asynchronous");
                
                if (isNonTeachingStaff && isTeachingHoliday)
                {
                    // Non-Teaching staff during teaching holiday
                    // Show actual calculated status instead of admin-time
                    Debug.WriteLine($"");
                    Debug.WriteLine($"? ADMIN-TIME OVERRIDE for Non-Teaching Staff:");
                    Debug.WriteLine($"   Employee: {_employee.FullName}");
                    Debug.WriteLine($"   Staff Type: {_employee.StaffType}");
                    Debug.WriteLine($"   Holiday Type: {holidayCheck.HolidayType}");
                    Debug.WriteLine($"   Original Status: admin-time");
                    Debug.WriteLine($"   Will display actual status instead (on-time/late/undertime)");
                    Debug.WriteLine($"");
                    
                    // Use the actual calculated status from validation
                    if (validation.IsLate)
                    {
                        status = "late";
                    }
                    else if (validation.IsEarlyOut)
                    {
                        status = "undertime";
                    }
                    else
                    {
                        status = "on-time";
                    }
                    
                    Debug.WriteLine($"   ? Corrected Status: {status}");
                }
            }
            
            var statusIcon = AttendanceStatusColors.GetIconByStatus(status);

            switch (status)
            {
                case "admin-time":
                    statusText = $"{statusIcon} ADMIN TIME";
                    statusColor = "#1565C0"; // Deep Blue
                    backgroundColor = "#E3F2FD"; // Light Blue
                    borderColor = "#2196F3"; // Blue
                    break;

                case "on-time":
                    statusText = $"{statusIcon} ON TIME";
                    statusColor = "#2E7D32"; // Dark Green
                    backgroundColor = "#E8F5E9"; // Light Green
                    borderColor = "#4CAF50"; // Green
                    break;

                case "late":
                    var lateHours = ((validation.LateMinutes ?? 0) / 60.0);
                    statusText = $"{statusIcon} LATE ({lateHours:0.00} HOURS)";
                    statusColor = "#C62828"; // Dark Red
                    backgroundColor = "#FFEBEE"; // Light Red
                    borderColor = "#F44336"; // Red
                    break;

                case "undertime":
                    var undertimeHours = ((validation.UndertimeMinutes ?? 0) / 60.0);
                    statusText = $"{statusIcon} UNDERTIME ({undertimeHours:0.00} HOURS)";
                    statusColor = "#E65100"; // Deep Orange
                    backgroundColor = "#FFF3E0"; // Light Orange
                    borderColor = "#FF9800"; // Orange
                    break;

                case "absent":
                    statusText = $"{statusIcon} ABSENT";
                    statusColor = "#424242"; // Dark Gray
                    backgroundColor = "#F5F5F5"; // Light Gray
                    borderColor = "#9E9E9E"; // Gray
                    break;

                default:
                    statusText = $"{statusIcon} RECORDED";
                    statusColor = "#1976D2"; // Blue
                    backgroundColor = "#E3F2FD"; // Light Blue
                    borderColor = "#2196F3"; // Blue
                    break;
            }
        }
        
        // Show and style the status badge
        StatusBadgeBorder.Visibility = Visibility.Visible;
        StatusBadgeBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)!);
        StatusBadgeBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor)!);
        StatusBadgeTextBlock.Text = statusText;
        StatusBadgeTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor)!);
        ApplyScheduleSourceBadge(BuildScheduleSourceText(validation));
        
        Debug.WriteLine($"? Status Badge Displayed:");
        Debug.WriteLine($"   Final Display: {statusText}");
    }

    private void StartCountdown()
    {
        _countdown = 3;
        UpdateCountdown();
        _countdownTimer.Start();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _countdown--;
        UpdateCountdown();

        if (_countdown <= 0)
        {
            _countdownTimer.Stop();
            DialogResult = true;
            Close();
        }
    }

    private void UpdateCountdown()
    {
        CountdownRun.Text = _countdown.ToString();
    }

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer?.Stop();

        base.OnClosed(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Allow ESC key to close the window
        if (e.Key == Key.Escape)
        {
            _countdownTimer?.Stop();
            DialogResult = true;
            Close();
            return;
        }

        // Allow Alt+F4 (do not block)
        // No handling needed - let it pass through
    }

    /// <summary>
    /// Load employee photo from photo_path column
    /// Supports: File paths (absolute/relative), URLs (http/https), and base64 encoded images
    /// </summary>
    private async Task LoadEmployeePhotoAsync()
    {
        try
        {
            BitmapImage? bitmap = null;

            if (_employee.EmployeeId > 0)
            {
                try
                {
                    var photoBytes = await App.DatabaseService.GetEmployeePhotoBytesAsync(_employee.EmployeeId);
                    if (photoBytes != null && photoBytes.Length > 0)
                    {
                        using var stream = new MemoryStream(photoBytes);
                        var fromDb = new BitmapImage();
                        fromDb.BeginInit();
                        fromDb.CacheOption = BitmapCacheOption.OnLoad;
                        fromDb.DecodePixelWidth = 220;
                        fromDb.StreamSource = stream;
                        fromDb.EndInit();
                        fromDb.Freeze();
                        bitmap = fromDb;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Profile] DB photo load failed: {ex.Message}");
                }
            }

            if (bitmap == null && !string.IsNullOrWhiteSpace(_employee.PhotoPath))
            {
                bitmap = await AsyncImageLoader.LoadAsync(_employee.PhotoPath, decodePixelWidth: 220);
            }

            if (bitmap == null)
            {
                ShowInitialFallback();
                return;
            }

            PhotoImage.Source = bitmap;
            PhotoImage.Visibility = Visibility.Visible;
            InitialViewbox.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Profile] Error loading photo: {ex.Message}");
            ShowInitialFallback();
        }
    }
    
    /// <summary>
    /// Load photo from a local file path
    /// </summary>
    private void LoadPhotoFromFile(string filePath)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            
            // Freeze for performance (allows cross-thread access)
            bitmap.Freeze();
            
            PhotoImage.Source = bitmap;
            PhotoImage.Visibility = Visibility.Visible;
            InitialViewbox.Visibility = Visibility.Collapsed;
            
            Debug.WriteLine($"? Photo loaded successfully from file!");
            Debug.WriteLine($"   Dimensions: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error loading photo from file: {ex.Message}");
            ShowInitialFallback();
        }
    }
    
    /// <summary>
    /// Load photo from a URL (http:// or https://)
    /// Loads asynchronously on a background thread to avoid blocking UI
    /// </summary>
    private async void LoadPhotoFromUrl(string url)
    {
        try
        {
            Debug.WriteLine($"   URL: {url}");
            
            // Download image on background thread
            await Task.Run(async () =>
            {
                try
                {
                    using var httpClient = new System.Net.Http.HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(10); // 10 second timeout
                    
                    Debug.WriteLine($"   ?? Downloading image from URL...");
                    var imageBytes = await httpClient.GetByteArrayAsync(url);
                    Debug.WriteLine($"   ? Downloaded {imageBytes.Length} bytes");
                    
                    // Switch back to UI thread to update UI
                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                            using (var stream = new System.IO.MemoryStream(imageBytes))
                            {
                                bitmap.BeginInit();
                                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = stream;
                                bitmap.EndInit();
                            }
                            
                            // Freeze for performance
                            bitmap.Freeze();
                            
                            PhotoImage.Source = bitmap;
                            PhotoImage.Visibility = Visibility.Visible;
                            InitialViewbox.Visibility = Visibility.Collapsed;
                            
                            Debug.WriteLine($"? Photo loaded successfully from URL!");
                            Debug.WriteLine($"   Dimensions: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                        }
                        catch (Exception innerEx)
                        {
                            Debug.WriteLine($"? Error creating bitmap from downloaded data: {innerEx.Message}");
                            ShowInitialFallback();
                        }
                    });
                }
                catch (System.Net.Http.HttpRequestException httpEx)
                {
                    Debug.WriteLine($"? HTTP Error loading photo from URL: {httpEx.Message}");
                    Debug.WriteLine($"   Status Code: {httpEx.StatusCode}");
                    Debug.WriteLine($"   This might be due to:");
                    Debug.WriteLine($"   - Network connectivity issues");
                    Debug.WriteLine($"   - URL is not accessible");
                    Debug.WriteLine($"   - CORS restrictions");
                    Debug.WriteLine($"   - Server is down");
                    
                    await Dispatcher.InvokeAsync(() => ShowInitialFallback());
                }
                catch (TaskCanceledException)
                {
                    Debug.WriteLine($"? Timeout loading photo from URL (10 seconds exceeded)");
                    await Dispatcher.InvokeAsync(() => ShowInitialFallback());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"? Error downloading photo from URL: {ex.Message}");
                    await Dispatcher.InvokeAsync(() => ShowInitialFallback());
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in LoadPhotoFromUrl: {ex.Message}");
            Debug.WriteLine($"   Stack trace: {ex.StackTrace}");
            ShowInitialFallback();
        }
    }
    
    /// <summary>
    /// Load photo from base64 encoded string
    /// Format: data:image/png;base64,iVBORw0KGgoAAAANTS...
    /// </summary>
    private void LoadPhotoFromBase64(string base64Data)
    {
        try
        {
            // Extract the base64 part (remove "data:image/xxx;base64," prefix)
            var base64String = base64Data;
            if (base64Data.Contains(","))
            {
                base64String = base64Data.Substring(base64Data.IndexOf(",") + 1);
            }
            
            // Convert base64 to byte array
            var imageBytes = Convert.FromBase64String(base64String);
            
            // Create BitmapImage from bytes
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            using (var stream = new System.IO.MemoryStream(imageBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            
            // Freeze for performance
            bitmap.Freeze();
            
            PhotoImage.Source = bitmap;
            PhotoImage.Visibility = Visibility.Visible;
            InitialViewbox.Visibility = Visibility.Collapsed;
            
            Debug.WriteLine($"? Photo loaded successfully from base64!");
            Debug.WriteLine($"   Size: {imageBytes.Length} bytes");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error loading photo from base64: {ex.Message}");
            ShowInitialFallback();
        }
    }
    
    /// <summary>
    /// Show initial letter as fallback when photo cannot be loaded
    /// </summary>
    private void ShowInitialFallback()
    {
        PhotoImage.Visibility = Visibility.Collapsed;
        InitialViewbox.Visibility = Visibility.Visible;
        Debug.WriteLine($"?? Showing initial letter fallback: {_employee.Initial}");
    }
    
    /// <summary>
    /// Display the ACTUAL TIME based on priority:
    /// 1. Holiday/Suspension Missed Log -> Show SCHEDULE TIME (10:00 AM)
    /// 2. Regular Missed Log -> Show REQUESTED TIME (6:52 AM) 
    /// 3. No Missed Log with Schedule -> Show ACTUAL TAP TIME (not schedule time)
    /// 4. No Schedule -> Show ACTUAL TAP TIME
    /// </summary>
    private async Task DisplayActualTimeAsync()
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"????????????????????????????????????");
            Debug.WriteLine($"? DISPLAYING ACTUAL TIME");
            Debug.WriteLine($"   Employee: {_employee.FullName}");
            Debug.WriteLine($"   Tap Time: {_tapTime:yyyy-MM-dd HH:mm:ss}");
            Debug.WriteLine($"   Tap Type: {_tapType}");
            Debug.WriteLine($"????????????????????????????????????");
            
            // STEP 1: Check for missed log verification request
            var missedLogValidator = new MissedLogValidationService(App.DatabaseService.GetConnectionString());
            var missedLogValidation = await missedLogValidator.ValidateMissedLogAsync(
                _employee.EmployeeId,
                _tapTime,
                _tapType
            );
            
            if (missedLogValidation.HasApprovedMissedLog && missedLogValidation.RequestedTime.HasValue)
            {
                // Check if this is a holiday/suspension missed log
                bool isHolidaySuspensionMissedLog = !string.IsNullOrWhiteSpace(missedLogValidation.Reason) &&
                    (missedLogValidation.Reason.Contains("holiday", StringComparison.OrdinalIgnoreCase) ||
                     missedLogValidation.Reason.Contains("suspension", StringComparison.OrdinalIgnoreCase) ||
                     missedLogValidation.Reason.Contains("online_class", StringComparison.OrdinalIgnoreCase) ||
                     missedLogValidation.Reason.Contains("online class", StringComparison.OrdinalIgnoreCase) ||
                     missedLogValidation.Reason.Contains("suspended_asynchronous", StringComparison.OrdinalIgnoreCase) ||
                     missedLogValidation.Reason.Contains("suspended asynchronous", StringComparison.OrdinalIgnoreCase));
                
                Debug.WriteLine($"? MISSED LOG FOUND:");
                Debug.WriteLine($"   Request ID: {missedLogValidation.RequestId}");
                Debug.WriteLine($"   Reason: {missedLogValidation.Reason}");
                Debug.WriteLine($"   Requested Time: {missedLogValidation.RequestedTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Is Holiday/Suspension: {isHolidaySuspensionMissedLog}");
                
                if (isHolidaySuspensionMissedLog)
                {
                    Debug.WriteLine($"? Holiday/Suspension missed log detected");
                    Debug.WriteLine($"   Will show SCHEDULE TIME instead of requested_time or tap time");
                    Debug.WriteLine($"   Continuing to check for schedule...");
                    
                    // For holiday/suspension, show the schedule time
                    var dynamicUpdater = new DynamicScheduleUpdater(App.DatabaseService.GetConnectionString());
                    var currentSchedule = await dynamicUpdater.GetCurrentExpectedScheduleAsync(_employee.EmployeeId, _tapTime.Date);

                    if (currentSchedule.HasSchedule && currentSchedule.ExpectedTimeIn.HasValue && currentSchedule.ExpectedTimeOut.HasValue)
                    {
                        var scheduleDate = DateTime.Today;
                        var expectedTime = _tapType == "IN" 
                            ? scheduleDate.Add(currentSchedule.ExpectedTimeIn.Value)
                            : scheduleDate.Add(currentSchedule.ExpectedTimeOut.Value);
                        
                        ActualTimeTextBlock.Text = expectedTime.ToString("hh:mm tt");
                        ActualTimeTextBlock.Foreground = GetStatusColor();
                        
                        Debug.WriteLine($"? Using SCHEDULE TIME for holiday/suspension missed log:");
                        Debug.WriteLine($"   Schedule Time: {expectedTime:hh:mm tt}");
                        Debug.WriteLine($"   (Not showing requested_time: {missedLogValidation.RequestedTime:hh:mm tt})");
                        Debug.WriteLine($"????????????????????????????????????");
                        return;
                    }
                }
                else
                {
                    // REGULAR MISSED LOG (not holiday/suspension) - Use requested_time
                    var requestedTime = missedLogValidation.RequestedTime.Value;
                    ActualTimeTextBlock.Text = requestedTime.ToString("hh:mm tt");
                    ActualTimeTextBlock.Foreground = GetStatusColor();
                    
                    Debug.WriteLine($"? Using REQUESTED TIME from regular missed log:");
                    Debug.WriteLine($"   Requested Time: {requestedTime:yyyy-MM-dd HH:mm:ss}");
                    Debug.WriteLine($"   Display: {requestedTime:hh:mm tt}");
                    Debug.WriteLine($"????????????????????????????????????");
                    return;
                }
            }
            
            // STEP 2: No missed log (or holiday/suspension missed log with no schedule)
            // Show ACTUAL TAP TIME (not schedule time!)
            ActualTimeTextBlock.Text = _tapTime.ToString("hh:mm tt");
            ActualTimeTextBlock.Foreground = GetStatusColor();
            
            Debug.WriteLine($"? Using ACTUAL TAP TIME:");
            Debug.WriteLine($"   Tap Time: {_tapTime:hh:mm tt}");
            Debug.WriteLine($"   (No missed log or holiday/suspension missed log fallback)");
            Debug.WriteLine($"????????????????????????????????????");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error displaying actual time: {ex.Message}");
            Debug.WriteLine($"   Stack: {ex.StackTrace}");
            // Fallback to tap time
            ActualTimeTextBlock.Text = _tapTime.ToString("hh:mm tt");
            ActualTimeTextBlock.Foreground = GetStatusColor();
        }
    }
}
