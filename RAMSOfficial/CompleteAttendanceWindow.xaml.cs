using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RAMSOfficial.Models;
using RAMSOfficial.Helpers;

namespace RAMSOfficial;

public partial class CompleteAttendanceWindow : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private int _countdown = 3;
    private readonly Employee _employee;
    private readonly AttendanceLog _timeInLog;
    private readonly AttendanceLog _timeOutLog;
    private readonly int _tapCount;

    public CompleteAttendanceWindow(
        Employee employee, 
        AttendanceLog timeInLog,
        AttendanceLog timeOutLog,
        int tapCount)
    {
        InitializeComponent();
        
        _employee = employee;
        _timeInLog = timeInLog;
        _timeOutLog = timeOutLog;
        _tapCount = tapCount;

        // Setup countdown timer
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadEmployeeDataAsync();
        StartCountdown();
    }

    private async Task LoadEmployeeDataAsync()
    {
        // Avatar
        InitialTextBlock.Text = _employee.Initial;
        await LoadEmployeePhotoAsync();

        // Name and ID
        NameTextBlock.Text = _employee.FullName;
        SchoolIdRun.Text = _employee.SchoolId ?? "N/A";

        // ? FIXED: Display StaffType below the name (Teaching or Non-Teaching)
        EmployeeTypeRun.Text = _employee.StaffType ?? _employee.EmployeeType ?? "Staff";

        // Details
        DepartmentTextBlock.Text = _employee.Department ?? "N/A";
        
        RoleRun.Text = _employee.StaffType ?? _employee.EmployeeType ?? "Staff";
        EmploymentStatusRun.Text = _employee.EmploymentStatus ?? "Active";

        // Date
        DateTextBlock.Text = _timeInLog.Date.ToString("dddd, MMMM dd, yyyy");
        await LoadExpectedScheduleAsync();

        // TIME IN
        TimeInTextBlock.Text = _timeInLog.LogTime.ToString("hh:mm tt");
        SetStatusBadge(
            TimeInStatusBorder,
            TimeInStatusTextBlock,
            TimeInSourceTextBlock,
            _timeInLog.AttendanceStatus ?? string.Empty,
            _timeInLog.LateMinutes,
            _timeInLog.Notes);
        ApplyRecordCardTheme(TimeInRecordBorder, _timeInLog.AttendanceStatus ?? string.Empty);

        // TIME OUT
        TimeOutTextBlock.Text = _timeOutLog.LogTime.ToString("hh:mm tt");
        SetStatusBadge(
            TimeOutStatusBorder,
            TimeOutStatusTextBlock,
            TimeOutSourceTextBlock,
            _timeOutLog.AttendanceStatus ?? string.Empty,
            _timeOutLog.UndertimeMinutes,
            _timeOutLog.Notes);
        ApplyRecordCardTheme(TimeOutRecordBorder, _timeOutLog.AttendanceStatus ?? string.Empty);

        // Tap count message
        TapCountTextBlock.Text = $"You have tapped {_tapCount} times today.\nYour attendance is already complete.";

        Debug.WriteLine($"✓ Complete Attendance Window Loaded:");
        Debug.WriteLine($"   Employee: {_employee.FullName}");
        Debug.WriteLine($"   TIME IN: {_timeInLog.LogTime:HH:mm} - {_timeInLog.AttendanceStatus}");
        Debug.WriteLine($"   TIME OUT: {_timeOutLog.LogTime:HH:mm} - {_timeOutLog.AttendanceStatus}");
        Debug.WriteLine($"   Tap Count: {_tapCount}");
    }

    private async Task LoadEmployeePhotoAsync()
    {
        try
        {
            BitmapImage? bitmap = null;

            if (_employee.EmployeeId > 0)
            {
                var photoBytes = await App.DatabaseService.GetEmployeePhotoBytesAsync(_employee.EmployeeId);
                if (photoBytes != null && photoBytes.Length > 0)
                {
                    using var stream = new MemoryStream(photoBytes);
                    var fromDb = new BitmapImage();
                    fromDb.BeginInit();
                    fromDb.CacheOption = BitmapCacheOption.OnLoad;
                    fromDb.DecodePixelWidth = 170;
                    fromDb.StreamSource = stream;
                    fromDb.EndInit();
                    fromDb.Freeze();
                    bitmap = fromDb;
                }
            }

            if (bitmap == null && !string.IsNullOrWhiteSpace(_employee.PhotoPath))
            {
                bitmap = await AsyncImageLoader.LoadAsync(_employee.PhotoPath, decodePixelWidth: 170);
            }

            if (bitmap != null)
            {
                PhotoImage.Source = bitmap;
                PhotoImage.Visibility = Visibility.Visible;
                InitialViewbox.Visibility = Visibility.Collapsed;
            }
            else
            {
                PhotoImage.Source = null;
                PhotoImage.Visibility = Visibility.Collapsed;
                InitialViewbox.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompleteAttendance] Photo load failed for {_employee.FullName}: {ex.Message}");
            PhotoImage.Source = null;
            PhotoImage.Visibility = Visibility.Collapsed;
            InitialViewbox.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadExpectedScheduleAsync()
    {
        try
        {
            var date = _timeInLog.Date.Date;

            var dynamicUpdater = new DynamicScheduleUpdater(App.DatabaseService.GetConnectionString());
            var currentSchedule = await dynamicUpdater.GetCurrentExpectedScheduleAsync(_employee.EmployeeId, date);

            if (currentSchedule.HasSchedule && currentSchedule.ExpectedTimeIn.HasValue && currentSchedule.ExpectedTimeOut.HasValue)
            {
                var scheduleDate = DateTime.Today;
                var expectedIn = scheduleDate.Add(currentSchedule.ExpectedTimeIn.Value);
                var expectedOut = scheduleDate.Add(currentSchedule.ExpectedTimeOut.Value);
                ExpectedScheduleTextBlock.Text = $"{expectedIn:hh:mm tt} → {expectedOut:hh:mm tt}";
                ExpectedScheduleBorder.Visibility = Visibility.Visible;
                return;
            }

            bool isNonTeaching = _employee.StaffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
            bool isRegularOrProvisionary = _employee.EmploymentStatus?.Contains("Regular", StringComparison.OrdinalIgnoreCase) == true ||
                                          _employee.EmploymentStatus?.Contains("Provisionary", StringComparison.OrdinalIgnoreCase) == true;

            if (isNonTeaching && isRegularOrProvisionary)
            {
                var scheduleDate = DateTime.Today;
                var expectedIn = scheduleDate.Add(_employee.ScheduleTimeIn);
                var expectedOut = scheduleDate.Add(_employee.ScheduleTimeOut);
                ExpectedScheduleTextBlock.Text = $"{expectedIn:hh:mm tt} → {expectedOut:hh:mm tt}";
                ExpectedScheduleBorder.Visibility = Visibility.Visible;
                return;
            }

            ExpectedScheduleTextBlock.Text = "No schedule for this day";
            ExpectedScheduleBorder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ Expected schedule load failed: {ex.Message}");
            ExpectedScheduleTextBlock.Text = "Schedule unavailable";
            ExpectedScheduleBorder.Visibility = Visibility.Visible;
        }
    }

    private void SetStatusBadge(Border border, TextBlock textBlock, TextBlock sourceTextBlock, string status, int? minutes, string? notes)
    {
        string statusText;
        string statusColor;
        string backgroundColor;
        string borderColor;
        
        var statusLower = NormalizeStatus(status);
        var statusIcon = AttendanceStatusColors.GetIconByStatus(statusLower);
        
        switch (statusLower)
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
                var lateHours = ((minutes ?? 0) / 60.0);
                statusText = $"{statusIcon} LATE ({lateHours:0.00} HOURS)";
                statusColor = "#C62828"; // Dark Red
                backgroundColor = "#FFEBEE"; // Light Red
                borderColor = "#F44336"; // Red
                break;

            case "undertime":
                var undertimeHours = ((minutes ?? 0) / 60.0);
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
        
        // Apply styles
        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)!);
        border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor)!);
        textBlock.Text = statusText;
        textBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor)!);

        var source = ExtractScheduleSourceFromNotes(notes);
        if (string.IsNullOrWhiteSpace(source))
        {
            sourceTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            sourceTextBlock.Text = source;
            sourceTextBlock.Visibility = Visibility.Visible;
        }
    }

    private static string? ExtractScheduleSourceFromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;

        const string marker = "Source:";
        var idx = notes.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var sourcePart = notes[(idx + marker.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(sourcePart)) return null;

        var nextSeparator = sourcePart.IndexOf('|');
        if (nextSeparator >= 0)
            sourcePart = sourcePart[..nextSeparator].Trim();

        return $"Schedule Source: {sourcePart}";
    }

    private static string NormalizeStatus(string status)
    {
        return (status ?? "on-time")
            .Trim()
            .ToLowerInvariant()
            .Replace("_", "-")
            .Replace(" ", "-");
    }

    private void ApplyRecordCardTheme(Border cardBorder, string status)
    {
        var normalized = NormalizeStatus(status);

        string bg;
        string border;

        switch (normalized)
        {
            case "on-time":
                bg = "#F0FDF4";
                border = "#86EFAC";
                break;
            case "late":
                bg = "#FEF2F2";
                border = "#FCA5A5";
                break;
            case "undertime":
                bg = "#FFF7ED";
                border = "#FDBA74";
                break;
            case "admin-time":
                bg = "#EFF6FF";
                border = "#93C5FD";
                break;
            case "absent":
                bg = "#F5F5F5";
                border = "#D4D4D4";
                break;
            default:
                bg = "#F8FAFC";
                border = "#CBD5E1";
                break;
        }

        cardBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)!);
        cardBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border)!);
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
        }
    }
}
