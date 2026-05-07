using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RAMSOfficial.Helpers;

namespace RAMSOfficial;

public partial class CustomMessageDialog : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private int _countdown = 3;
    private readonly MessageType _messageType;
    private bool _isClosed; // guard against double-close from timer + button race

    public enum MessageType
    {
        Warning,
        Error,
        Info,
        Success,
        Block,
        Holiday,
        ScheduleWarning // Clock-Alert icon for schedule-related issues
    }

    public CustomMessageDialog(
        string title,
        string message,
        string? details = null,
        MessageType type = MessageType.Warning,
        int countdownSeconds = 3)
    {
        InitializeComponent();
        
        _messageType = type;
        _countdown = countdownSeconds;
        
        // Set content
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        
        if (!string.IsNullOrEmpty(details))
        {
            DetailsTextBlock.Text = details;
            DetailsBorder.Visibility = Visibility.Visible;
        }
        
        // Configure appearance based on type
        ConfigureAppearance(type);
        
        // Setup countdown timer
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
        
        UpdateCountdown();
    }

    private void ConfigureAppearance(MessageType type)
    {
        switch (type)
        {
            case MessageType.Error:
                HeaderBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336")!);
                TitleTextBlock.Text = "ERROR";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C")!);
                // X cross icon
                ConfigureIcon("M30,30 L70,70 M70,30 L30,70", "#F44336");
                break;

            case MessageType.Warning:
                HeaderBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800")!);
                TitleTextBlock.Text = "WARNING";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C2410C")!);
                // Exclamation in triangle: vertical bar + dot
                ConfigureIcon("M50,15 L10,85 L90,85 Z M50,35 L50,60 M50,68 L50,74", "#FF9800");
                break;

            case MessageType.Info:
                HeaderBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3")!);
                TitleTextBlock.Text = "INFORMATION";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B5CAD")!);
                // Info: dot + vertical bar
                ConfigureIcon("M50,25 L50,32 M50,40 L50,75", "#2196F3");
                break;

            case MessageType.Success:
                HeaderBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")!);
                TitleTextBlock.Text = "SUCCESS";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534")!);
                // Checkmark
                ConfigureIcon("M25,52 L42,68 L75,32", "#4CAF50");
                break;

            case MessageType.Block:
                HeaderBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9C27B0")!);
                TitleTextBlock.Text = "ACCESS BLOCKED";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B21A8")!);
                // Shield-X: circle + diagonal slash
                ConfigureIcon("M50,10 A40,40 0 1,0 50,90 A40,40 0 1,0 50,10 M28,28 L72,72", "#9C27B0");
                break;

            case MessageType.Holiday:
                HeaderBorder.Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#FF6F00")!,
                    (Color)ColorConverter.ConvertFromString("#FF9800")!,
                    90);
                TitleTextBlock.Text = "SPECIAL NOTICE";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A3412")!);
                // Calendar: rectangle + header bar + rows
                ConfigureIcon("M20,25 L80,25 L80,80 L20,80 Z M20,35 L80,35 M40,25 L40,18 M60,25 L60,18 M35,48 L45,48 M55,48 L65,48 M35,62 L45,62 M55,62 L65,62", "#FF9800");
                break;

            case MessageType.ScheduleWarning:
                HeaderBorder.Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#E65100")!,
                    (Color)ColorConverter.ConvertFromString("#FF9800")!,
                    45);
                TitleTextBlock.Text = "SCHEDULE WARNING";
                OkButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A3412")!);
                // Clock-alert: circle + hands + exclamation
                ConfigureIcon("M50,10 A40,40 0 1,1 49.99,10 M50,50 L50,28 M50,50 L65,50 M80,70 L80,82 M80,88 L80,90", "#FF9800");
                break;
        }
    }

    private void ConfigureIcon(string pathData, string color)
    {
        IconPath.Data = Geometry.Parse(pathData);
        IconPath.Stroke = Brushes.White;
        // Use thinner stroke for complex paths (calendar, shield), heavier for simple shapes
        IconPath.StrokeThickness = pathData.Length > 80 ? 4 : 8;
        IconPath.StrokeStartLineCap = System.Windows.Media.PenLineCap.Round;
        IconPath.StrokeEndLineCap = System.Windows.Media.PenLineCap.Round;
        IconPath.StrokeLineJoin = PenLineJoin.Round;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Play fade-in animation
        var storyboard = (Storyboard)FindResource("FadeInAnimation");
        storyboard.Begin(this);

        // Ensure window is topmost and activated
        this.Topmost = true;
        this.Activate();

        // Start countdown
        _countdownTimer.Start();

        // Auto-scroll to bottom so countdown is always visible
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ContentScrollViewer.ScrollableHeight > 0)
            {
                var scrollAnimation = new DoubleAnimation(
                    0,
                    ContentScrollViewer.ScrollableHeight,
                    TimeSpan.FromSeconds(Math.Min(_countdown, 2)));
                scrollAnimation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };

                // WPF ScrollViewer doesn't directly support animation on VerticalOffset,
                // so use a timer-based smooth scroll instead
                var scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                var scrollStart = DateTime.Now;
                var scrollDuration = TimeSpan.FromSeconds(Math.Min(_countdown, 1.5));
                var targetOffset = ContentScrollViewer.ScrollableHeight;

                scrollTimer.Tick += (s, args) =>
                {
                    var elapsed = DateTime.Now - scrollStart;
                    var progress = Math.Min(1.0, elapsed.TotalMilliseconds / scrollDuration.TotalMilliseconds);
                    // Ease-in-out
                    var eased = progress < 0.5
                        ? 2 * progress * progress
                        : 1 - Math.Pow(-2 * progress + 2, 2) / 2;
                    ContentScrollViewer.ScrollToVerticalOffset(eased * targetOffset);

                    if (progress >= 1.0)
                    {
                        ((DispatcherTimer)s!).Stop();
                    }
                };
                scrollTimer.Start();
            }
        }));
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _countdown--;
        UpdateCountdown();

        if (_countdown <= 0)
        {
            SafeClose();
        }
    }

    private void UpdateCountdown()
    {
        CountdownRun.Text = _countdown.ToString();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SafeClose();
    }

    /// <summary>
    /// Cancel-safe close: stops the timer and closes exactly once,
    /// even if the timer tick and the button click fire near-simultaneously.
    /// Plays a short fade-out before closing for polish.
    /// </summary>
    private void SafeClose()
    {
        if (_isClosed) return;
        _isClosed = true;
        _countdownTimer.Stop();

        // Play fade-out, then close
        try
        {
            var fadeOut = (Storyboard)FindResource("FadeOutAnimation");
            fadeOut.Completed += (_, _) =>
            {
                DialogResult = true;
                Close();
            };
            fadeOut.Begin(this);
        }
        catch
        {
            // If animation resource missing, close immediately
            DialogResult = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _countdownTimer?.Stop();
        base.OnClosed(e);
    }

    // ???????????????????????????????????????????????????????
    // STATIC HELPER METHODS - GENERAL
    // ???????????????????????????????????????????????????????

    public static void ShowError(string message, string? details = null, int countdown = 3)
    {
        var dialog = new CustomMessageDialog(
            "Error",
            message,
            details,
            MessageType.Error,
            countdown);
        dialog.ShowDialog();
    }

    public static void ShowWarning(string message, string? details = null, int countdown = 3)
    {
        var dialog = new CustomMessageDialog(
            "Warning",
            message,
            details,
            MessageType.Warning,
            countdown);
        dialog.ShowDialog();
    }

    public static void ShowInfo(string message, string? details = null, int countdown = 3)
    {
        var dialog = new CustomMessageDialog(
            "Information",
            message,
            details,
            MessageType.Info,
            countdown);
        dialog.ShowDialog();
    }

    public static void ShowSuccess(string message, string? details = null, int countdown = 3)
    {
        var dialog = new CustomMessageDialog(
            "Success",
            message,
            details,
            MessageType.Success,
            countdown);
        dialog.ShowDialog();
    }

    public static void ShowBlock(string message, string? details = null, int countdown = 3)
    {
        var dialog = new CustomMessageDialog(
            "Blocked",
            message,
            details,
            MessageType.Block,
            countdown);
        dialog.ShowDialog();
    }

    public static void ShowScheduleWarning(string message, string? details = null, int countdown = 5)
    {
        var dialog = new CustomMessageDialog(
            "No Schedule",
            message,
            details,
            MessageType.ScheduleWarning,
            countdown);
        dialog.ShowDialog();
    }

    // ???????????????????????????????????????????????????????
    // HOLIDAY-SPECIFIC WARNING DIALOGS
    // ???????????????????????????????????????????????????????

    /// <summary>
    /// Show warning for Suspended Asynchronous (Class Suspension - Reporting) holiday
    /// </summary>
    public static void ShowReportingDayWarning(string holidayName, string staffType, string employmentStatus)
    {
        var isTeaching = staffType.Equals("Teaching", StringComparison.OrdinalIgnoreCase);
        var isNonTeaching = staffType.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase);
        var isPartTimeFull = employmentStatus.Contains("Part", StringComparison.OrdinalIgnoreCase) && 
                            employmentStatus.Contains("Full Load", StringComparison.OrdinalIgnoreCase);
        var isRegular = employmentStatus.Equals("Regular", StringComparison.OrdinalIgnoreCase);

        string message;
        string details;

        if (isNonTeaching)
        {
            message = $"TODAY IS REPORTING DAY\n\n" +
                     $"{holidayName}\n\n" +
                     $"Classes are SUSPENDED but you must REPORT as usual.";
            
            details = "NON-TEACHING STAFF REQUIREMENTS:\n\n" +
                     $"• You are: {staffType} - {employmentStatus}\n" +
                     $"• Attendance Status: Will be calculated normally\n" +
                     $"  • Late/On Time/Undertime based on actual tap time\n\n" +
                     $"• Follow your regular working schedule\n" +
                     $"• Report to your designated area\n" +
                     $"• NO ADMIN TIME for Non-Teaching staff";
        }
        else if (isTeaching && (isPartTimeFull || isRegular))
        {
            message = $"TODAY IS REPORTING DAY\n\n" +
                     $"{holidayName}\n\n" +
                     $"Classes are SUSPENDED but reporting staff must be present.";
            
            details = $"TEACHING STAFF (REPORTING REQUIRED):\n\n" +
                     $"• You are: {staffType} - {employmentStatus}\n" +
                     $"• Attendance Status: ADMIN TIME\n" +
                     $"  • No late/undertime calculation\n\n" +
                     $"• Report to your designated area\n" +
                     $"• Participate in alternative duties\n" +
                     $"• Your attendance is recorded as Admin Time";
        }
        else
        {
            message = $"TODAY IS REPORTING DAY\n\n" +
                     $"{holidayName}\n\n" +
                     $"Classes are SUSPENDED.";
            
            details = $"PART-TIME TEACHING STAFF:\n\n" +
                     $"• You are: {staffType} - {employmentStatus}\n" +
                     $"• Attendance Status: Will be calculated normally\n" +
                     $"  • Late/On Time/Undertime based on actual tap time\n\n" +
                     $"• Follow your regular schedule\n" +
                     $"• No reporting required for Part-Time staff";
        }

        var dialog = new CustomMessageDialog(
            "Reporting Day Notice",
            message,
            details,
            MessageType.Holiday,
            5); // 5 seconds for important notice
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show warning for Online Class holiday
    /// </summary>
    public static void ShowOnlineClassWarning(string holidayName, string staffType, string employmentStatus)
    {
        var isTeaching = staffType.Equals("Teaching", StringComparison.OrdinalIgnoreCase);
        var isNonTeaching = staffType.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase);
        var isPartTimeFull = employmentStatus.Contains("Part", StringComparison.OrdinalIgnoreCase) && 
                            employmentStatus.Contains("Full Load", StringComparison.OrdinalIgnoreCase);

        string message;
        string details;

        if (isNonTeaching)
        {
            message = $"ONLINE CLASS DAY\n\n" +
                     $"{holidayName}\n\n" +
                     $"Classes are conducted ONLINE but you must REPORT.";
            
            details = "NON-TEACHING STAFF REQUIREMENTS:\n\n" +
                     $"• You are: {staffType} - {employmentStatus}\n" +
                     $"• Attendance Status: Will be calculated normally\n" +
                     $"  • Late/On Time/Undertime based on actual tap time\n\n" +
                     $"• Follow your regular working schedule\n" +
                     $"• Support online class operations\n" +
                     $"• NO ADMIN TIME for Non-Teaching staff";
        }
        else if (isTeaching && isPartTimeFull)
        {
            message = $"ONLINE CLASS DAY\n\n" +
                     $"{holidayName}\n\n" +
                     $"Classes are conducted ONLINE.";
            
            details = $"PART-TIME FULL LOAD TEACHING STAFF:\n\n" +
                     $"• You are: {staffType} - {employmentStatus}\n" +
                     $"• Attendance Status: ADMIN TIME\n" +
                     $"  • No late/undertime calculation\n\n" +
                     $"• Conduct classes online from home/office\n" +
                     $"• Physical reporting not required\n" +
                     $"• Your attendance is recorded as Admin Time";
        }
        else
        {
            message = $"ONLINE CLASS DAY\n\n" +
                     $"{holidayName}\n\n" +
                     $"Classes are conducted ONLINE.";
            
            details = $"REGULAR TEACHING STAFF:\n\n" +
                     $"• You are: {staffType} - {employmentStatus}\n" +
                     $"• Attendance Status: Will be calculated normally\n" +
                     $"  • Late/On Time/Undertime based on actual tap time\n\n" +
                     $"• Conduct classes online\n" +
                     $"• Follow regular schedule";
        }

        var dialog = new CustomMessageDialog(
            "Online Class Notice",
            message,
            details,
            MessageType.Holiday,
            5);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show warning for Regular Holiday
    /// </summary>
    public static void ShowRegularHolidayWarning(string holidayName, string staffType)
    {
        var isNonTeaching = staffType.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase);

        string message;
        string details;

        if (isNonTeaching)
        {
            message = $"HOLIDAY NOTICE\n\n" +
                     $"{holidayName}\n\n" +
                     $"Today is a HOLIDAY but you are tapping in.";
            
            details = "NON-TEACHING STAFF:\n\n" +
                     $"• You are: {staffType}\n" +
                     $"• Attendance Status: Will be calculated normally\n" +
                     $"  • Late/On Time/Undertime based on actual tap time\n\n" +
                     $"• Holiday pay rules apply\n" +
                     $"• Check with HR for holiday compensation";
        }
        else
        {
            message = $"HOLIDAY NOTICE\n\n" +
                     $"{holidayName}\n\n" +
                     $"Today is a HOLIDAY.";
            
            details = "TEACHING STAFF:\n\n" +
                     $"• You are: {staffType}\n" +
                     $"• Attendance Status: ADMIN TIME\n" +
                     $"  • No late/undertime calculation\n\n" +
                     $"• No classes scheduled\n" +
                     $"• Special duty assignment (if any)";
        }

        var dialog = new CustomMessageDialog(
            "Holiday Notice",
            message,
            details,
            MessageType.Holiday,
            4);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show warning for Leave (approved)
    /// </summary>
    public static void ShowLeaveNotice(string leaveReason, string leaveType = "Leave")
    {
        var message = $"LEAVE REQUEST APPROVED\n\n" +
                     $"You have an approved {leaveType.ToLower()} for today.";
        
        var details = $"LEAVE DETAILS:\n\n" +
                     $"• Type: {leaveType}\n" +
                     $"• Reason: {ReasonTextFormatter.ToFormal(leaveReason)}\n" +
                     $"• Attendance Status: ADMIN TIME\n\n" +
                     $"Your attendance is automatically recorded.\n" +
                     $"No need to tap in/out today.";

        var dialog = new CustomMessageDialog(
            "Leave Notice",
            message,
            details,
            MessageType.Info,
            4);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show warning for Missed Log (approved)
    /// </summary>
    public static void ShowMissedLogNotice(string reason, DateTime requestedTime)
    {
        var message = $"MISSED LOG REQUEST APPROVED\n\n" +
                     $"Your missed log request has been approved.";
        
        var details = $"MISSED LOG DETAILS:\n\n" +
                     $"• Requested Time: {requestedTime:hh:mm tt}\n" +
                     $"• Reason: {ReasonTextFormatter.ToFormal(reason)}\n" +
                     $"• Attendance Status: Based on requested time\n\n" +
                     $"Your attendance is recorded with the requested time.";

        var dialog = new CustomMessageDialog(
            "Missed Log Notice",
            message,
            details,
            MessageType.Info,
            3);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Master method to show appropriate holiday warning based on validation result
    /// </summary>
    public static void ShowHolidayWarningIfNeeded(
        Services.AttendanceValidationResult validation,
        string employeeFullName,
        string staffType,
        string employmentStatus)
    {
        if (!validation.IsHoliday || string.IsNullOrEmpty(validation.HolidayType))
            return;

        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine($"?? SHOWING HOLIDAY WARNING DIALOG");
        System.Diagnostics.Debug.WriteLine($"   Holiday Type: {validation.HolidayType}");
        System.Diagnostics.Debug.WriteLine($"   Schedule Details: {validation.ScheduleDetails}");
        System.Diagnostics.Debug.WriteLine($"   Employee: {employeeFullName}");
        System.Diagnostics.Debug.WriteLine($"   Staff Type: {staffType}");
        System.Diagnostics.Debug.WriteLine($"   Employment Status: {employmentStatus}");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");

        var holidayName = validation.ScheduleDetails;
        var holidayType = validation.HolidayType.ToLowerInvariant();

        switch (holidayType)
        {
            case "suspended_asynchronous":
                ShowReportingDayWarning(holidayName, staffType, employmentStatus);
                break;

            case "online_class":
                ShowOnlineClassWarning(holidayName, staffType, employmentStatus);
                break;

            case "regular":
            case "special":
            case "holiday":
                ShowRegularHolidayWarning(holidayName, staffType);
                break;

            default:
                // Generic holiday warning
                ShowInfo(
                    $"SPECIAL DAY NOTICE\n\n{holidayName}",
                    $"Type: {validation.HolidayType}\n" +
                    $"Employee: {employeeFullName}\n" +
                    $"Status: {validation.AttendanceStatus}",
                    4);
                break;
        }

        System.Diagnostics.Debug.WriteLine($"? Holiday warning dialog shown successfully");
    }
}
