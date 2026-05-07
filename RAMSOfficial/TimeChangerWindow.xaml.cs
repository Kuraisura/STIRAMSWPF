using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace RAMSOfficial;

/// <summary>
/// Time Override Window - Testing Tool for RFID Attendance System
/// Allows setting custom date/time for testing scenarios
/// </summary>
public partial class TimeChangerWindow : Window
{
    private const string AccessPassword = "121121212112@Aa";
    private static DateTime? _customTime = null;
    private static DateTime _customTimeSetAt = DateTime.MinValue;
    private readonly DispatcherTimer _updateTimer;
    private bool _isAccessUnlocked;
    private TaskCompletionSource<bool>? _confirmDialogTcs;
    private TaskCompletionSource<bool>? _noticeDialogTcs;

    // ???????????????????????????????????????????????????????????????
    // STATIC METHODS - Replace DateTime.Now Throughout Application
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Returns current time - either custom override or real time
    /// USE THIS INSTEAD OF DateTime.Now EVERYWHERE!
    /// </summary>
    public static DateTime GetCurrentTime()
    {
        if (_customTime.HasValue)
        {
            // Calculate elapsed time since custom time was set
            var elapsed = DateTime.Now - _customTimeSetAt;
            return _customTime.Value.Add(elapsed);
        }
        
        return DateTime.Now;
    }

    /// <summary>
    /// Check if time is currently overridden
    /// Use for visual indicators and sync control
    /// </summary>
    public static bool IsTimeOverridden()
    {
        return _customTime.HasValue;
    }

    /// <summary>
    /// Set custom time override
    /// </summary>
    public static void SetCustomTime(DateTime customTime)
    {
        _customTime = customTime;
        _customTimeSetAt = DateTime.Now;
        
        // Clear employee tap state when time is changed
        CustomTimeStateManager.ClearState();
        
        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("?????????????????????????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("?  ?? TIME OVERRIDE ACTIVATED                          ?");
        System.Diagnostics.Debug.WriteLine($"?  Custom Time: {customTime:yyyy-MM-dd HH:mm:ss,-33} ?");
        System.Diagnostics.Debug.WriteLine($"?  Day of Week: {customTime.DayOfWeek,-41} ?");
        System.Diagnostics.Debug.WriteLine("?  ??  Database sync is PAUSED                         ?");
        System.Diagnostics.Debug.WriteLine("?????????????????????????????????????????????????????????");
    }

    /// <summary>
    /// Clear custom time override - return to real time
    /// </summary>
    public static void ClearCustomTime()
    {
        _customTime = null;
        _customTimeSetAt = DateTime.MinValue;
        
        // Clear employee tap state when override is cleared
        CustomTimeStateManager.ClearState();
        
        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("?????????????????????????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("?  ? TIME OVERRIDE CLEARED                            ?");
        System.Diagnostics.Debug.WriteLine("?  Returned to real time                               ?");
        System.Diagnostics.Debug.WriteLine("?  ? Database sync is RESUMED                         ?");
        System.Diagnostics.Debug.WriteLine("?????????????????????????????????????????????????????????");
    }

    // ???????????????????????????????????????????????????????????????
    // INSTANCE CONSTRUCTOR AND METHODS
    // ???????????????????????????????????????????????????????????????

    public TimeChangerWindow()
    {
        InitializeComponent();
        
        // Setup timer to update current time display
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        
        // Initialize with current time
        var now = GetCurrentTime();
        CustomDatePicker.SelectedDate = now.Date;
        HourTextBox.Text = now.Hour.ToString("D2");
        MinuteTextBox.Text = now.Minute.ToString("D2");
        
        // Don't call UpdatePreview() here - UI elements not ready yet
        // UpdateStatus() will be called in Window_Loaded
    }
    
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Play fade-in animation
        var storyboard = (Storyboard)FindResource("FadeInAnimation");
        storyboard.Begin(MainBorder);
        
        // Start update timer
        _updateTimer.Start();
        
        // Now safe to update UI elements
        UpdatePreview();
        UpdateStatus();

        SetAccessState(false);

        // Focus password field first
        AccessPasswordBox.Focus();
    }

    private void SetAccessState(bool unlocked)
    {
        _isAccessUnlocked = unlocked;

        CustomDatePicker.IsEnabled = unlocked;
        HourTextBox.IsEnabled = unlocked;
        MinuteTextBox.IsEnabled = unlocked;
        SetButton.IsEnabled = unlocked;
        ClearButton.IsEnabled = unlocked;

        if (unlocked)
        {
            AccessErrorText.Visibility = Visibility.Collapsed;
            UnlockAccessButton.IsEnabled = false;
            AccessPasswordBox.IsEnabled = false;

            HourTextBox.Focus();
            HourTextBox.SelectAll();
        }
    }

    private void UnlockAccessButton_Click(object sender, RoutedEventArgs e)
    {
        ValidateAccessPassword();
    }

    private void AccessPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ValidateAccessPassword();
        }
    }

    private void ValidateAccessPassword()
    {
        if (_isAccessUnlocked)
            return;

        if (AccessPasswordBox.Password == AccessPassword)
        {
            SetAccessState(true);
            return;
        }

        AccessErrorText.Visibility = Visibility.Visible;
        AccessPasswordBox.Clear();
        AccessPasswordBox.Focus();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var currentTime = GetCurrentTime();
        CurrentTimeText.Text = currentTime.ToString("yyyy-MM-dd hh:mm tt");
        
        if (IsTimeOverridden())
        {
            StatusModeText.Text = "TIME OVERRIDE ACTIVE";
            StatusModeText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF9800")!);
        }
        else
        {
            StatusModeText.Text = "Real Time";
            StatusModeText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50")!);
        }
    }

    private void CustomDatePicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CustomDatePicker.SelectedDate.HasValue)
        {
            var dayOfWeek = CustomDatePicker.SelectedDate.Value.DayOfWeek;
            DayOfWeekText.Text = $"Day: {dayOfWeek}";
            
            // Highlight Sunday in red
            if (dayOfWeek == DayOfWeek.Sunday)
            {
                DayOfWeekText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F44336")!);
                DayOfWeekText.Text = $"Day: {dayOfWeek} (Attendance blocked on Sundays)";
            }
            else
            {
                DayOfWeekText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888888")!);
            }
        }
        
        UpdatePreview();
    }

    private void TimeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        // Safety check - ensure PreviewText is initialized
        if (PreviewText == null)
            return;
            
        try
        {
            if (CustomDatePicker.SelectedDate.HasValue &&
                int.TryParse(HourTextBox.Text, out int hour) &&
                int.TryParse(MinuteTextBox.Text, out int minute))
            {
                // Validate ranges
                if (hour >= 0 && hour <= 23 &&
                    minute >= 0 && minute <= 59)
                {
                    var selectedDate = CustomDatePicker.SelectedDate.Value;
                    var previewTime = new DateTime(
                        selectedDate.Year,
                        selectedDate.Month,
                        selectedDate.Day,
                        hour,
                        minute,
                        0); // Seconds always 0
                    
                    PreviewText.Text = previewTime.ToString("dddd, MMMM dd, yyyy hh:mm tt");
                    PreviewText.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0D47A1")!);
                    
                    return;
                }
            }
            
            PreviewText.Text = "Invalid time values";
            PreviewText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F44336")!);
        }
        catch
        {
            PreviewText.Text = "Invalid date/time";
            PreviewText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F44336")!);
        }
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only allow numeric input
        var regex = new Regex("[^0-9]+");
        e.Handled = regex.IsMatch(e.Text);
    }

    private async void SetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isAccessUnlocked)
        {
            await ShowCustomNoticeDialogAsync("Access Required", "Enter the Time Changer password first.", "OK");
            AccessPasswordBox.Focus();
            return;
        }

        try
        {
            if (!CustomDatePicker.SelectedDate.HasValue)
            {
                await ShowCustomNoticeDialogAsync("Validation Error", "Please select a date.", "OK");
                return;
            }
            
            if (!int.TryParse(HourTextBox.Text, out int hour) ||
                !int.TryParse(MinuteTextBox.Text, out int minute))
            {
                await ShowCustomNoticeDialogAsync("Validation Error", "Please enter valid time values.", "OK");
                return;
            }
            
            // Validate ranges
            if (hour < 0 || hour > 23)
            {
                await ShowCustomNoticeDialogAsync("Validation Error", "Hour must be between 0 and 23.", "OK");
                HourTextBox.Focus();
                HourTextBox.SelectAll();
                return;
            }
            
            if (minute < 0 || minute > 59)
            {
                await ShowCustomNoticeDialogAsync("Validation Error", "Minute must be between 0 and 59.", "OK");
                MinuteTextBox.Focus();
                MinuteTextBox.SelectAll();
                return;
            }
            
            // Create custom time with seconds set to 0
            var selectedDate = CustomDatePicker.SelectedDate.Value;
            var customTime = new DateTime(
                selectedDate.Year,
                selectedDate.Month,
                selectedDate.Day,
                hour,
                minute,
                0); // Seconds always 0
            
            var confirmed = await ShowCustomConfirmDialogAsync(
                "Confirm Time Override",
                $"Set time override to:\n\n{customTime:dddd, MMMM dd, yyyy hh:mm tt}\n\n" +
                "? WARNING:\n" +
                "• All timestamps will use this custom date and time\n" +
                "• Attendance will be recorded with this exact time\n" +
                "• Status (Late/On Time) will be calculated based on this time\n" +
                "• Only use for testing!\n\n" +
                "Continue?",
                "Set Override");

            if (confirmed)
            {
                SetCustomTime(customTime);
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            await ShowCustomNoticeDialogAsync("Error", $"Error setting custom time:\n\n{ex.Message}", "OK");
        }
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isAccessUnlocked)
        {
            await ShowCustomNoticeDialogAsync("Access Required", "Enter the Time Changer password first.", "OK");
            AccessPasswordBox.Focus();
            return;
        }

        if (!IsTimeOverridden())
        {
            await ShowCustomNoticeDialogAsync("Info", "Time override is not currently active.", "OK");
            return;
        }
        
        await ConfirmClearOverrideAsync();
    }

    private async Task ConfirmClearOverrideAsync()
    {
        var confirmed = await ShowCustomConfirmDialogAsync(
            "Confirm Clear Override",
            "Clear time override and return to real time?\n\n• All timestamps will use real time.",
            "Clear Override");

        if (confirmed)
        {
            ClearCustomTime();
            DialogResult = true;
            Close();
        }
    }

    private Task<bool> ShowCustomConfirmDialogAsync(string title, string message, string yesText)
    {
        ConfirmTitleText.Text = title;
        ConfirmMessageText.Text = message;
        ConfirmYesButton.Content = yesText;
        ConfirmOverlay.Visibility = Visibility.Visible;

        _confirmDialogTcs = new TaskCompletionSource<bool>();

        Dispatcher.BeginInvoke(() => ConfirmYesButton.Focus(), DispatcherPriority.Background);
        return _confirmDialogTcs.Task;
    }

    private void ConfirmYesButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmDialogTcs?.TrySetResult(true);
    }

    private void ConfirmNoButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmDialogTcs?.TrySetResult(false);
    }

    private Task ShowCustomNoticeDialogAsync(string title, string message, string okText)
    {
        var titleText = FindName("NoticeTitleText") as TextBlock;
        var messageText = FindName("NoticeMessageText") as TextBlock;
        var okButton = FindName("NoticeOkButton") as Button;
        var overlay = FindName("NoticeOverlay") as Grid;

        if (titleText != null) titleText.Text = title;
        if (messageText != null) messageText.Text = message;
        if (okButton != null) okButton.Content = okText;
        if (overlay != null) overlay.Visibility = Visibility.Visible;

        _noticeDialogTcs = new TaskCompletionSource<bool>();
        if (okButton != null)
            Dispatcher.BeginInvoke(() => okButton.Focus(), DispatcherPriority.Background);
        return _noticeDialogTcs.Task;
    }

    private void NoticeOkButton_Click(object sender, RoutedEventArgs e)
    {
        var overlay = FindName("NoticeOverlay") as Grid;
        if (overlay != null) overlay.Visibility = Visibility.Collapsed;
        _noticeDialogTcs?.TrySetResult(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var noticeOverlay = FindName("NoticeOverlay") as Grid;
        if (e.Key == Key.Escape && noticeOverlay?.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            NoticeOkButton_Click(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.Escape && ConfirmOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            ConfirmNoButton_Click(this, new RoutedEventArgs());
            return;
        }

        // Allow Escape to close the window
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        
        // Allow Alt+F4 (no blocking)
    }

    protected override void OnClosed(EventArgs e)
    {
        _updateTimer?.Stop();
        base.OnClosed(e);
    }
}
