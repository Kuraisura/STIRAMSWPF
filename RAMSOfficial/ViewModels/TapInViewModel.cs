using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RAMSOfficial.Helpers;
using RAMSOfficial.Models;
using RAMSOfficial.Services;

namespace RAMSOfficial.ViewModels;

/// <summary>
/// Possible visual states for the Tap-In screen.
/// The code-behind listens for <see cref="CurrentState"/> changes and triggers
/// the corresponding GPU-composited storyboard (no Visibility toggling).
/// </summary>
public enum TapInState
{
    WaitingForTap,
    Processing,
    Success,
    Error
}

/// <summary>
/// MVVM ViewModel for the Employee Tap-In kiosk screen.
///
/// CommunityToolkit.Mvvm for property/command infrastructure.
///
/// Database strategy (via <see cref="HybridAttendanceSyncService"/>):
///   OFFLINE / UNSTABLE → Write to local STIRAMS.db immediately (offline-first).
///   ONLINE / STABLE    → Write to database too (dual backup).
///   ALWAYS             → Every tap ends up in BOTH databases.
///
/// Animation model:
///   The ViewModel only sets <see cref="CurrentState"/> and populates data properties.
///   The code-behind (<c>TapInWindow.xaml.cs</c>) observes <c>CurrentState</c> via
///   <c>PropertyChanged</c> and triggers GPU storyboards — keeping the ViewModel
///   completely UI-framework-agnostic.
///
/// Auto-hide / debounce:
///   The 10-second countdown is owned by the code-behind's DispatcherTimer.
///   When it fires it calls <see cref="RequestClearSensitiveData"/> which wipes
///   all employee fields from memory (A03 compliance).
///
/// Security:
///   A03 – <see cref="RequestClearSensitiveData"/>: wipes employee info after 10 s.
///   A10 – Offline taps queued locally and synced via background timer.
///   Input sanitisation via <see cref="InputSanitizer"/>.
/// </summary>
public class TapInViewModel : ObservableObject, IDisposable
{
    // ───────── timers ─────────
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _feedRefreshTimer;

    // ───────── sensitive-data buffer (A03) ─────────
    private SecureString? _sensitiveBuffer;

    // ───────── backing fields ─────────
    private TapInState _currentState = TapInState.WaitingForTap;
    private string _employeeName = string.Empty;
    private string _employeeIdDisplay = string.Empty;
    private string _department = string.Empty;
    private string _tapDirection = string.Empty;
    private string _tapTimestamp = string.Empty;
    private bool _isOnline = true;
    private int _pendingQueueCount;
    private string _errorMessage = string.Empty;
    private string _currentTime = string.Empty;
    private string _currentDate = string.Empty;
    private string _statusMessage = "Tap your RFID card to check in or out";
    private bool _isSavedOfflineOnly;
    private string _employeeInitial = string.Empty;
    private BitmapImage? _employeePhoto;
    private bool _hasPhoto;

    // ───────── recent attendance feed ─────────
    private ObservableCollection<RecentAttendanceItem> _recentAttendance = new();

    // ═══════════════════ Properties ═══════════════════

    public TapInState CurrentState
    {
        get => _currentState;
        set
        {
            if (SetProperty(ref _currentState, value))
            {
                OnPropertyChanged(nameof(IsProcessing));
                OnPropertyChanged(nameof(IsShowingResult));
            }
        }
    }

    public string EmployeeName
    {
        get => _employeeName;
        set => SetProperty(ref _employeeName, value);
    }

    public string EmployeeIdDisplay
    {
        get => _employeeIdDisplay;
        set => SetProperty(ref _employeeIdDisplay, value);
    }

    public string Department
    {
        get => _department;
        set => SetProperty(ref _department, value);
    }

    public string TapDirection
    {
        get => _tapDirection;
        set
        {
            if (SetProperty(ref _tapDirection, value))
                OnPropertyChanged(nameof(IsTapIn));
        }
    }

    public string TapTimestamp
    {
        get => _tapTimestamp;
        set => SetProperty(ref _tapTimestamp, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetProperty(ref _isOnline, value);
    }

    public int PendingQueueCount
    {
        get => _pendingQueueCount;
        set
        {
            if (SetProperty(ref _pendingQueueCount, value))
                OnPropertyChanged(nameof(HasPendingItems));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string CurrentTime
    {
        get => _currentTime;
        set => SetProperty(ref _currentTime, value);
    }

    public string CurrentDate
    {
        get => _currentDate;
        set => SetProperty(ref _currentDate, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsSavedOfflineOnly
    {
        get => _isSavedOfflineOnly;
        set => SetProperty(ref _isSavedOfflineOnly, value);
    }

    /// <summary>First letter of the employee name (avatar fallback).</summary>
    public string EmployeeInitial
    {
        get => _employeeInitial;
        set => SetProperty(ref _employeeInitial, value);
    }

    /// <summary>
    /// Downsampled employee photo loaded asynchronously. Null when no photo is available.
    /// The <see cref="BitmapImage"/> is frozen so it's safe for cross-thread binding.
    /// </summary>
    public BitmapImage? EmployeePhoto
    {
        get => _employeePhoto;
        set
        {
            if (SetProperty(ref _employeePhoto, value))
                HasPhoto = value != null;
        }
    }

    public bool HasPhoto
    {
        get => _hasPhoto;
        set => SetProperty(ref _hasPhoto, value);
    }

    /// <summary>Observable collection of today's recent attendance for the sidebar feed.</summary>
    public ObservableCollection<RecentAttendanceItem> RecentAttendance
    {
        get => _recentAttendance;
        set => SetProperty(ref _recentAttendance, value);
    }

    /// <summary>True when there are items in the recent attendance feed.</summary>
    public bool HasRecentAttendance => RecentAttendance.Count > 0;

    // ═══════════════════ Computed Properties ═══════════════════

    /// <summary>Used by the processing spinner visibility in XAML.</summary>
    public bool IsProcessing => CurrentState == TapInState.Processing;

    /// <summary>True when any result card is (or should be) visible.</summary>
    public bool IsShowingResult => CurrentState is TapInState.Success or TapInState.Error;

    public bool IsTapIn => string.Equals(TapDirection, "IN", StringComparison.OrdinalIgnoreCase);
    public bool HasPendingItems => PendingQueueCount > 0;

    // ═══════════════════ Commands ═══════════════════

    public IAsyncRelayCommand<string> ProcessRfidCommand { get; }

    // ═══════════════════ Constructor ═══════════════════

    public TapInViewModel()
    {
        ProcessRfidCommand = new AsyncRelayCommand<string>(ProcessRfidTapAsync);

        // Clock display (1 Hz)
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        // Feed refresh (30s) to pick up new remote names without app restart
        _feedRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _feedRefreshTimer.Tick += async (_, _) => await RefreshRecentAttendanceAsync();
        _feedRefreshTimer.Start();

        // Network status relay
        if (App.AttendanceSyncService != null)
        {
            App.AttendanceSyncService.ConnectivityChanged += (_, online) =>
            {
                Application.Current?.Dispatcher.Invoke(() => IsOnline = online);
            };

            // Pending queue count relay
            App.AttendanceSyncService.PendingCountChanged += (_, count) =>
            {
                Application.Current?.Dispatcher.Invoke(() => PendingQueueCount = count);
            };
        }
    }

    /// <summary>Initialize and load initial queue state.</summary>
    public async Task InitializeAsync()
    {
        if (App.AttendanceSyncService != null)
        {
            IsOnline = App.AttendanceSyncService.IsOnline;
            PendingQueueCount = await App.AttendanceSyncService.GetPendingCountAsync();
        }

        // Load the recent attendance sidebar feed
        await RefreshRecentAttendanceAsync();
    }

    // ═══════════════════ Core Logic ═══════════════════

    /// <summary>
    /// Validate, sanitise, and forward an RFID UID to the hybrid attendance service.
    ///
    /// Flow:
    ///   1. Input sanitisation (anti RFID-injection)
    ///   2. Employee lookup + IN/OUT via <see cref="HybridAttendanceSyncService"/>
    ///   3. Record to STIRAMS.db first, then database (if online)
    ///   4. Set <see cref="CurrentState"/> → code-behind triggers storyboard
    ///   5. Auto-hide timer in code-behind handles the 10 s countdown + debounce
    /// </summary>
    public async Task ProcessRfidTapAsync(string? rfidUid)
    {
        if (string.IsNullOrWhiteSpace(rfidUid)) return;
        if (CurrentState == TapInState.Processing) return;

        // Wipe previous data immediately
        WipeSensitiveFields();

        // ── Input Sanitization ──
        var sanitized = InputSanitizer.SanitizeRfidUid(rfidUid);
        if (!InputSanitizer.IsValidRfidUid(sanitized))
        {
            ErrorMessage = "Invalid card data. Please try again.";
            StatusMessage = "Card read error";
            CurrentState = TapInState.Error;
            Debug.WriteLine($"[TapIn] RFID validation failed (length={rfidUid.Length}).");
            return;
        }

        CurrentState = TapInState.Processing;
        StatusMessage = "Processing…";

        try
        {
            var result = await App.AttendanceSyncService.ProcessTapAsync(sanitized);

            if (result.Success)
            {
                StoreSensitiveData(result.EmployeeName);

                EmployeeName = result.EmployeeName;
                EmployeeIdDisplay = result.EmployeeId;
                Department = result.Department;
                TapDirection = result.TapDirection;
                TapTimestamp = result.Timestamp.ToString("hh:mm:ss tt");
                IsSavedOfflineOnly = result.IsQueued;

                // Avatar initial (first letter of name)
                EmployeeInitial = string.IsNullOrEmpty(result.EmployeeName)
                    ? "?"
                    : result.EmployeeName[..1].ToUpper();

                // Async photo load (non-blocking)
                _ = LoadEmployeePhotoAsync(result.PhotoPath, result.EmployeeNumericId);

                CurrentState = TapInState.Success;

                // Graceful overview: when Time Out is recorded, show both In and Out times
                if (result.TapDirection == "OUT" && result.TimeInTimestamp != null)
                {
                    var inTime = DateTime.TryParse(result.TimeInTimestamp, out var inDt)
                        ? inDt.ToString("hh:mm tt") : result.TimeInTimestamp;
                    StatusMessage = $"Attendance Overview — In: {inTime}, Out: {result.Timestamp:hh:mm tt}";
                }
                else
                {
                    StatusMessage = $"Tap {result.TapDirection} recorded successfully";
                }

                // Refresh the recent attendance sidebar (fire-and-forget)
                _ = RefreshRecentAttendanceAsync();
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Unknown error.";
                StatusMessage = "Tap failed";
                CurrentState = TapInState.Error;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Unexpected error processing tap.";
            StatusMessage = "System error";
            CurrentState = TapInState.Error;
            Debug.WriteLine($"[TapIn] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the employee's photo in the background using
    /// <see cref="AsyncImageLoader"/> (DecodePixelWidth = 200).
    /// The result is a frozen <see cref="BitmapImage"/> safe for UI binding.
    /// </summary>
    private async Task LoadEmployeePhotoAsync(string? photoPath, int employeeNumericId)
    {
        EmployeePhoto = null;

        BitmapImage? bitmap = null;

        // Prefer database bytes to avoid stale URL/cache mismatches.
        if (employeeNumericId > 0)
        {
            try
            {
                var photoBytes = await App.DatabaseService.GetEmployeePhotoBytesAsync(employeeNumericId);
                if (photoBytes != null && photoBytes.Length > 0)
                {
                    using var stream = new MemoryStream(photoBytes);
                    var fromDb = new BitmapImage();
                    fromDb.BeginInit();
                    fromDb.CacheOption = BitmapCacheOption.OnLoad;
                    fromDb.DecodePixelWidth = 200;
                    fromDb.StreamSource = stream;
                    fromDb.EndInit();
                    fromDb.Freeze();
                    bitmap = fromDb;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TapIn] DB photo load failed: {ex.Message}");
            }
        }

        // Fallback to URL/path only when DB bytes are not available.
        if (bitmap == null && !string.IsNullOrWhiteSpace(photoPath))
            bitmap = await AsyncImageLoader.LoadAsync(photoPath, decodePixelWidth: 200);

        // Only apply if the card is still showing (user might have tapped again)
        if (CurrentState == TapInState.Success)
        {
            EmployeePhoto = bitmap;
        }
    }

    // ═══════════════════ A03: Sensitive Data ═══════════════════

    /// <summary>
    /// Called by the code-behind's auto-hide timer after 10 seconds.
    /// Wipes all employee data and returns to idle.
    /// </summary>
    public void RequestClearSensitiveData()
    {
        WipeSensitiveFields();
        GC.Collect(0, GCCollectionMode.Optimized);

        CurrentState = TapInState.WaitingForTap;
        StatusMessage = "Tap your RFID card to check in or out";

        Debug.WriteLine("[TapIn] Sensitive data cleared from UI and memory.");
    }

    private void WipeSensitiveFields()
    {
        EmployeeName = string.Empty;
        EmployeeIdDisplay = string.Empty;
        Department = string.Empty;
        TapDirection = string.Empty;
        TapTimestamp = string.Empty;
        ErrorMessage = string.Empty;
        IsSavedOfflineOnly = false;
        EmployeeInitial = string.Empty;
        EmployeePhoto = null;

        if (_sensitiveBuffer != null)
        {
            _sensitiveBuffer.Dispose();
            _sensitiveBuffer = null;
        }
    }

    private void StoreSensitiveData(string data)
    {
        _sensitiveBuffer?.Dispose();
        _sensitiveBuffer = new SecureString();
        foreach (var c in data)
            _sensitiveBuffer.AppendChar(c);
        _sensitiveBuffer.MakeReadOnly();
    }

    // ═══════════════════ Recent Attendance Feed ═══════════════════

    /// <summary>
    /// Loads the latest attendance records from the database into the sidebar feed.
    /// Also asynchronously loads profile photos for each item.
    /// </summary>
    public async Task RefreshRecentAttendanceAsync()
    {
        try
        {
            if (App.AttendanceSyncService == null) return;
            var items = await App.AttendanceSyncService.GetRecentAttendanceAsync(15);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                RecentAttendance.Clear();
                foreach (var item in items)
                    RecentAttendance.Add(item);

                OnPropertyChanged(nameof(HasRecentAttendance));
            });

            // Load photos for each item asynchronously (non-blocking)
            _ = LoadRecentAttendancePhotosAsync(items);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TapIn] Error loading recent attendance: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads photos for recent attendance items in the background.
    /// Limits concurrent loads and uses small decode size for thumbnails.
    /// </summary>
    private async Task LoadRecentAttendancePhotosAsync(List<RecentAttendanceItem> items)
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
                    photo = await AsyncImageLoader.LoadAsync(item.PhotoPath, decodePixelWidth: 80);

                if (photo != null)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        item.Photo = photo;
                        // Force the collection to re-evaluate this item
                        var idx = RecentAttendance.IndexOf(item);
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
                Debug.WriteLine($"[TapIn] Photo load failed for {item.FullName}: {ex.Message}");
            }
        }
    }

    // ═══════════════════ Helpers ═══════════════════

    private void UpdateClock()
    {
        CurrentTime = DateTime.Now.ToString("hh:mm tt");
        CurrentDate = DateTime.Now.ToString("MMMM dd, yyyy");
    }

    // ═══════════════════ Dispose ═══════════════════

    public void Dispose()
    {
        _clockTimer.Stop();
        _feedRefreshTimer.Stop();
        WipeSensitiveFields();
    }
}
