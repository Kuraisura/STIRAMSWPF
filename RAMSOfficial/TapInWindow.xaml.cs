using System.Diagnostics;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RAMSOfficial.ViewModels;

namespace RAMSOfficial;

/// <summary>
/// Code-behind for the Employee Tap-In kiosk window.
///
/// Performance-critical design:
///   • Profile/Error cards are ALWAYS in the visual tree (pre-rendered, Opacity=0).
///   • Show/Hide uses GPU-composited Storyboards (RenderTransform + Opacity only).
///   • BitmapCache on both cards prevents per-frame re-rasterisation.
///   • Employee photos are loaded async with DecodePixelWidth via AsyncImageLoader.
///
/// Animation lifecycle:
///   1. RFID received → ViewModel populates fields → code-behind triggers CardShowAnimation.
///   2. 10-second DispatcherTimer starts (visible as a countdown ProgressBar in XAML).
///   3. If a NEW tap arrives within 10 s, timer resets (debounce) and card re-triggers.
///   4. Timer expires → CardHideAnimation plays → ViewModel.ClearSensitiveData().
///
/// Sound:
///   Success/Error chimes play asynchronously via <see cref="SoundPlayer"/> so they
///   never block the UI thread.
/// </summary>
public partial class TapInWindow : Window
{
    private readonly TapInViewModel _viewModel;

    // ── RFID keyboard-emulation buffer ──
    private readonly StringBuilder _rfidBuffer = new();
    private DateTime _lastKeyPress = DateTime.MinValue;
    private const int RfidTimeoutMs = 300;

    // ── Focus guard (kiosk mode) ──
    private readonly DispatcherTimer _focusGuardTimer;

    // ── Auto-hide timer (10 s, debounced) ──
    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _wipeDelayTimer;
    private const int AutoHideSeconds = 10;

    // ── Cached storyboards (resolved once in Window_Loaded) ──
    private Storyboard? _cardShow;
    private Storyboard? _cardHide;
    private Storyboard? _errorShow;
    private Storyboard? _errorHide;
    private Storyboard? _spinnerPulse;

    // ── Track which overlay is currently visible ──
    private enum VisibleCard { None, Profile, Error }
    private VisibleCard _activeCard = VisibleCard.None;

    // ── Async sound players ──
    private SoundPlayer? _successSound;
    private SoundPlayer? _errorSound;

    public TapInWindow()
    {
        InitializeComponent();

        _viewModel = new TapInViewModel();
        DataContext = _viewModel;

        // Focus guard — keeps keyboard input locked to the hidden TextBox
        _focusGuardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _focusGuardTimer.Tick += (_, _) => EnsureRfidFocus();
        _focusGuardTimer.Start();

        // Auto-hide: fires once after 10 s, resets on new tap (debounce)
        _autoHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AutoHideSeconds)
        };
        _autoHideTimer.Tick += AutoHideTimer_Tick;

        // Wipe delay: fires once 250 ms after the hide animation to clear sensitive data
        _wipeDelayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _wipeDelayTimer.Tick += WipeDelayTimer_Tick;

        // Subscribe to ViewModel state changes so we can trigger storyboards
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    // ═══════════════════ Lifecycle ═══════════════════

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Resolve storyboard resources (once)
        _cardShow = (Storyboard)FindResource("CardShowAnimation");
        _cardHide = (Storyboard)FindResource("CardHideAnimation");
        _errorShow = (Storyboard)FindResource("ErrorShowAnimation");
        _errorHide = (Storyboard)FindResource("ErrorHideAnimation");
        _spinnerPulse = (Storyboard)FindResource("SpinnerPulse");

        // Pre-load sound files (non-blocking)
        LoadSoundsAsync();

        await _viewModel.InitializeAsync();
        EnsureRfidFocus();
        Debug.WriteLine("[TapInWindow] Loaded — cards pre-rendered, storyboards cached.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _focusGuardTimer.Stop();
        _autoHideTimer.Stop();
        _wipeDelayTimer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        _successSound?.Dispose();
        _errorSound?.Dispose();
        base.OnClosed(e);
    }

    // ═══════════════════ ViewModel → Animation Bridge ═══════════════════

    /// <summary>
    /// Listens for <see cref="TapInViewModel.CurrentState"/> changes and
    /// triggers the appropriate GPU storyboard. This is the bridge between
    /// MVVM data and WPF visual-layer animations.
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TapInViewModel.CurrentState))
            return;

        switch (_viewModel.CurrentState)
        {
            case TapInState.Processing:
                HideActiveCard();
                _spinnerPulse?.Begin(this, true);
                break;

            case TapInState.Success:
                _spinnerPulse?.Stop(this);
                ShowProfileCard();
                PlaySuccessSound();
                break;

            case TapInState.Error:
                _spinnerPulse?.Stop(this);
                ShowErrorCard();
                PlayErrorSound();
                break;

            case TapInState.WaitingForTap:
                // Triggered by the auto-hide ClearSensitiveData path
                // Card is already hidden by the timer tick; nothing else to do.
                _spinnerPulse?.Stop(this);
                break;
        }
    }

    // ═══════════════════ Card Show / Hide (GPU Storyboards) ═══════════════════

    private void ShowProfileCard()
    {
        // If error card is visible, hide it first (instant — no animation overlap)
        if (_activeCard == VisibleCard.Error)
        {
            ErrorCard.Opacity = 0;
            ErrorCard.IsHitTestVisible = false;
        }

        _activeCard = VisibleCard.Profile;
        ProfileCard.IsHitTestVisible = true;
        _cardShow?.Begin(this, true);

        // Start (or restart) the 10-second debounce timer
        RestartAutoHideTimer();
    }

    private void ShowErrorCard()
    {
        if (_activeCard == VisibleCard.Profile)
        {
            ProfileCard.Opacity = 0;
            ProfileCard.IsHitTestVisible = false;
        }

        _activeCard = VisibleCard.Error;
        ErrorCard.IsHitTestVisible = true;
        _errorShow?.Begin(this, true);

        RestartAutoHideTimer();
    }

    /// <summary>
    /// Hides whichever card is currently showing via its hide storyboard.
    /// Called when the auto-hide timer fires.
    /// </summary>
    private void HideActiveCard()
    {
        switch (_activeCard)
        {
            case VisibleCard.Profile:
                ProfileCard.IsHitTestVisible = false;
                _cardHide?.Begin(this, true);
                break;
            case VisibleCard.Error:
                ErrorCard.IsHitTestVisible = false;
                _errorHide?.Begin(this, true);
                break;
        }
        _activeCard = VisibleCard.None;
    }

    // ═══════════════════ 10-Second Debouncing Auto-Hide ═══════════════════

    /// <summary>
    /// Resets the 10-second countdown. If a new tap arrives before the timer
    /// fires, the countdown restarts — the employee sees fresh data for
    /// another full 10 seconds.
    /// </summary>
    private void RestartAutoHideTimer()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    /// <summary>
    /// Fires once after 10 seconds of no new taps. Plays the hide animation
    /// then tells the ViewModel to wipe sensitive data (A03).
    /// </summary>
    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        HideActiveCard();

        // Give the hide animation 250 ms to finish, then wipe data
        _wipeDelayTimer.Stop();
        _wipeDelayTimer.Start();
    }

    private void WipeDelayTimer_Tick(object? sender, EventArgs e)
    {
        _wipeDelayTimer.Stop();
        _viewModel.RequestClearSensitiveData();
    }

    // ═══════════════════ Async Sound ═══════════════════

    private void LoadSoundsAsync()
    {
        // Load sounds on a thread-pool thread so Window_Loaded is never blocked.
        // If the files don't exist, the players stay null and Play*Sound() is a no-op.
        Task.Run(() =>
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var successPath = System.IO.Path.Combine(baseDir, "Assets", "Sounds", "success.wav");
                var errorPath = System.IO.Path.Combine(baseDir, "Assets", "Sounds", "error.wav");

                if (System.IO.File.Exists(successPath))
                {
                    _successSound = new SoundPlayer(successPath);
                    _successSound.Load();
                    Debug.WriteLine("[TapInWindow] Success sound loaded.");
                }

                if (System.IO.File.Exists(errorPath))
                {
                    _errorSound = new SoundPlayer(errorPath);
                    _errorSound.Load();
                    Debug.WriteLine("[TapInWindow] Error sound loaded.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TapInWindow] Sound load failed (non-critical): {ex.Message}");
            }
        });
    }

    private void PlaySuccessSound()
    {
        try { _successSound?.Play(); } // Play() is non-blocking for pre-loaded sounds
        catch { /* sound is non-critical */ }
    }

    private void PlayErrorSound()
    {
        try { _errorSound?.Play(); }
        catch { /* sound is non-critical */ }
    }

    // ═══════════════════ RFID Keyboard-Emulation ═══════════════════

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ESC closes the kiosk window and returns to MainWindow
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Debug.WriteLine("[TapInWindow] ESC pressed — closing kiosk.");
            Close();
            return;
        }

        // SPACEBAR opens manual ID input dialog
        if (e.Key == Key.Space && _rfidBuffer.Length == 0)
        {
            e.Handled = true;
            OpenManualIdInput();
            return;
        }

        var now = DateTime.Now;

        if ((now - _lastKeyPress).TotalMilliseconds > RfidTimeoutMs)
            _rfidBuffer.Clear();

        _lastKeyPress = now;

        if (e.Key is Key.Return or Key.Enter)
        {
            var code = _rfidBuffer.ToString().Trim();
            _rfidBuffer.Clear();

            // RFID UIDs must be at least 4 characters; shorter bursts are noise.
            if (code.Length >= 4)
            {
                Debug.WriteLine($"[TapInWindow] RFID buffer complete: {code}");
                _ = _viewModel.ProcessRfidTapAsync(code);
            }
            else if (!string.IsNullOrEmpty(code))
            {
                Debug.WriteLine($"[TapInWindow] RFID buffer too short ({code.Length} chars), ignoring.");
            }

            e.Handled = true;
            return;
        }

        var ch = KeyToChar(e.Key);
        if (ch.HasValue)
        {
            _rfidBuffer.Append(ch.Value);
            e.Handled = true;
        }
    }

    // ═══════════════════ Manual ID Input ═══════════════════

    private void OpenManualIdInput()
    {
        try
        {
            _focusGuardTimer.Stop();

            var dialog = new ManualIdInputDialog
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var result = dialog.ShowDialog();

            if (result == true && dialog.WasSubmitted && !string.IsNullOrWhiteSpace(dialog.EnteredId))
            {
                Debug.WriteLine($"[TapInWindow] Manual ID entered: {dialog.EnteredId}");
                _ = _viewModel.ProcessRfidTapAsync(dialog.EnteredId);
            }

            _focusGuardTimer.Start();
            EnsureRfidFocus();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TapInWindow] Manual input dialog error: {ex.Message}");
            _focusGuardTimer.Start();
        }
    }

    // ═══════════════════ Helpers ═══════════════════

    private void EnsureRfidFocus()
    {
        if (!RfidInputBox.IsFocused)
            RfidInputBox.Focus();
    }

    private static char? KeyToChar(Key key)
    {
        // Digits — top row and numpad
        if (key >= Key.D0 && key <= Key.D9) return (char)('0' + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (char)('0' + (key - Key.NumPad0));
        // Letters — most RFID readers emit uppercase via HID, but accept both
        if (key >= Key.A && key <= Key.Z) return (char)('A' + (key - Key.A));
        // Lowercase fallback for readers that send lowercase hex digits
        if (key >= Key.A && key <= Key.F) return (char)('a' + (key - Key.A));
        return null;
    }
}
