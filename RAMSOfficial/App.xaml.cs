using System.Windows;
using System.Windows.Threading;
using RAMSOfficial.Services;
using System.Diagnostics;

namespace RAMSOfficial;

public partial class App : Application
{
    public static DatabaseService DatabaseService { get; private set; } = null!;
    public static HybridDatabaseService HybridDatabase { get; private set; } = null!;
    public static ScheduleCacheService ScheduleCache { get; } = new();
    public static HybridAttendanceSyncService AttendanceSyncService { get; private set; } = null!;

    private SimpleRfidDetectionService? _rfidDetectionService;
    private DispatcherTimer? _readerCheckTimer;
    private Window? _currentWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Debug.WriteLine("???????????????????????????????????????????");
        Debug.WriteLine("  APPLICATION STARTING");
        Debug.WriteLine("???????????????????????????????????????????");

        // CRITICAL: Prevent app from closing when last window is closed
        // This allows us to switch between windows without the app exiting
        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Debug.WriteLine("? ShutdownMode set to OnExplicitShutdown");

        // Initialize database services
        DatabaseService = new DatabaseService();
        HybridDatabase = new HybridDatabaseService();

        // Initialize diagnostic logger early
        DiagnosticLogger.Initialize();

        try
        {
            // ── 1. Initialize HybridDatabase (SQLite schema + network monitoring) ──
            await HybridDatabase.InitializeAsync();

            // ── 2. AttendanceSyncService: THE SINGLE unified attendance processor ──
            //    Handles: ProcessTap, sync pending, pull metadata, migrate stuck records
            //    Warms ScheduleCache during init, starts 30s background sync timer
            AttendanceSyncService = new HybridAttendanceSyncService();
            await AttendanceSyncService.InitializeAsync();

            // If local database was empty and we still have no data, try PopulateLocalDatabase
            var hasCachedData = await HybridDatabase.HasCachedDataAsync();
            if (!hasCachedData)
            {
                Debug.WriteLine("");
                Debug.WriteLine("⚠ Local database is EMPTY after sync!");
                Debug.WriteLine("   Attempting to populate from database...");

                try
                {
                    await Tools.PopulateLocalDatabase.PopulateAsync();
                    Debug.WriteLine("✅ Local database populated successfully");

                    // Re-warm RAM cache after population
                    await ScheduleCache.RefreshFromLocalDbAsync(
                        $"Data Source={HybridDatabase.GetDatabasePath()}");
                }
                catch (Exception popEx)
                {
                    Debug.WriteLine($"❌ Failed to populate local database: {popEx.Message}");
                    MessageBox.Show(
                        "Could not populate local database from database.\n\n" +
                        "The system requires an internet connection for first-time setup.\n\n" +
                        $"Error: {popEx.Message}",
                        "Database Population Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
            }

            // Check if database tables exist (non-blocking for offline mode)
            try
            {
                var tablesExist = await DatabaseService.InitializeDatabaseAsync();

                if (!tablesExist)
                {
                    MessageBox.Show(
                        "Database tables 'employees' or 'attendance_logs' not found!\n\n" +
                        "Please ensure your database database has these tables.\n" +
                        "Check GetRAMSTableSchema.sql for details.",
                        "Database Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
                else
                {
                    Debug.WriteLine("Online database tables verified successfully");
                }
            }
            catch (Exception dbCheckEx)
            {
                Debug.WriteLine($"Online database check skipped (will use offline mode): {dbCheckEx.Message}");

                var hasCached = await HybridDatabase.HasCachedDataAsync();
                if (!hasCached)
                {
                    MessageBox.Show(
                        "Cannot connect to the online database and no local cache exists.\n\n" +
                        "Please ensure you have an internet connection for first-time setup.\n\n" +
                        $"Error: {dbCheckEx.Message}",
                        "Connection Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error initializing databases: {ex.Message}");
            MessageBox.Show(
                $"Database initialization failed:\n\n{ex.Message}\n\n" +
                "The app will start in OFFLINE mode if possible.",
                "Database Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }

        // Initialize RFID reader detection
        InitializeRfidDetection();

        // Check for reader and show appropriate window
        CheckReaderAndShowWindow();
    }

    private void InitializeRfidDetection()
    {
        Debug.WriteLine("?? Initializing RFID reader detection...");

        // Create detection service
        _rfidDetectionService = new SimpleRfidDetectionService();
        _rfidDetectionService.ReaderConnectionChanged += OnReaderConnectionChanged;

        // Create timer to periodically check for reader (every 2 seconds)
        _readerCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _readerCheckTimer.Tick += ReaderCheckTimer_Tick;
        _readerCheckTimer.Start();

        Debug.WriteLine("? RFID detection initialized");
    }

    private void CheckReaderAndShowWindow()
    {
        Debug.WriteLine("?? Checking for RFID reader...");

        var isReaderConnected = _rfidDetectionService?.CheckForReaderSync() ?? false;

        if (isReaderConnected)
        {
            Debug.WriteLine("? RFID reader detected - showing MainWindow");
            ShowMainWindow();
        }
        else
        {
            Debug.WriteLine("? RFID reader NOT detected - showing WaitingForReaderWindow");
            ShowWaitingForReaderWindow();
        }
    }

    private void ReaderCheckTimer_Tick(object? sender, EventArgs e)
    {
        // Periodically check for reader
        _rfidDetectionService?.CheckForReaderSync();
    }

    private void OnReaderConnectionChanged(object? sender, bool isConnected)
    {
        Debug.WriteLine("");
        Debug.WriteLine("?????????????????????????????????????????????");
        Debug.WriteLine($"  RFID READER STATUS CHANGED: {isConnected}");
        Debug.WriteLine("?????????????????????????????????????????????");

        Dispatcher.Invoke(() =>
        {
            if (isConnected)
            {
                Debug.WriteLine("? Reader connected - switching to MainWindow");
                ShowMainWindow();
            }
            else
            {
                Debug.WriteLine("? Reader disconnected - switching to WaitingForReaderWindow");
                ShowWaitingForReaderWindow();
            }
        });
    }

    public void SkipReaderCheck()
    {
        Debug.WriteLine("Skipping RFID reader check (Manual Override)");

        // Stop forcing the check
        _readerCheckTimer?.Stop();
        if (_rfidDetectionService != null)
        {
            _rfidDetectionService.ReaderConnectionChanged -= OnReaderConnectionChanged;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        // Close current window if it's NOT MainWindow
        if (_currentWindow != null && !(_currentWindow is MainWindow))
        {
            Debug.WriteLine($"Closing current window: {_currentWindow.GetType().Name}");
            
            // Unsubscribe from Closed event to prevent premature shutdown
            _currentWindow.Closed -= OnWindowClosed;
            _currentWindow.Close();
            _currentWindow = null;
        }

        // Only create MainWindow if it doesn't exist or was closed
        if (_currentWindow == null || !(_currentWindow is MainWindow))
        {
            Debug.WriteLine("Opening MainWindow...");
            _currentWindow = new MainWindow();
            
            // Subscribe to window closed event
            _currentWindow.Closed += OnWindowClosed;
            
            _currentWindow.Show();
            Debug.WriteLine("? MainWindow is now visible and active");
        }
        else
        {
            Debug.WriteLine("MainWindow already open - activating it");
            _currentWindow.Activate();
        }
    }

    private void ShowWaitingForReaderWindow()
    {
        // Close current window if it's NOT WaitingForReaderWindow
        if (_currentWindow != null && !(_currentWindow is WaitingForReaderWindow))
        {
            Debug.WriteLine($"Closing current window: {_currentWindow.GetType().Name}");
            
            // Unsubscribe from Closed event to prevent premature shutdown
            _currentWindow.Closed -= OnWindowClosed;
            _currentWindow.Close();
            _currentWindow = null;
        }

        // Only create WaitingForReaderWindow if it doesn't exist or was closed
        if (_currentWindow == null || !(_currentWindow is WaitingForReaderWindow))
        {
            Debug.WriteLine("Opening WaitingForReaderWindow (Waiting for Reader)...");
            _currentWindow = new WaitingForReaderWindow();
            
            // Subscribe to window closed event
            _currentWindow.Closed += OnWindowClosed;
            
            _currentWindow.Show();
            Debug.WriteLine("? WaitingForReaderWindow is now visible and active");
        }
        else
        {
            Debug.WriteLine("WaitingForReaderWindow already open - activating it");
            _currentWindow.Activate();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window closedWindow)
            return;

        // Only react to the tracked primary window.
        // Child dialogs/tools (Developer Console, Time Changer, etc.) must not terminate app flow.
        if (!ReferenceEquals(closedWindow, _currentWindow))
            return;

        Debug.WriteLine($"⚠ Primary window closed ({closedWindow.GetType().Name}) - recovering application window");

        _currentWindow.Closed -= OnWindowClosed;
        _currentWindow = null;

        // Keep application alive and show the appropriate screen instead of shutting down.
        CheckReaderAndShowWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Debug.WriteLine("???????????????????????????????????????");
        Debug.WriteLine("  APPLICATION SHUTTING DOWN");
        Debug.WriteLine("???????????????????????????????????????");

        // Stop timer
        _readerCheckTimer?.Stop();

        // Dispose detection service
        _rfidDetectionService?.Dispose();

        base.OnExit(e);
    }
}
