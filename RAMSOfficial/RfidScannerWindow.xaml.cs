using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace RAMSOfficial;

/// <summary>
/// RFID Scanner Window - WAITING FOR READER STATE
/// This window shows when the RFID reader is NOT connected
/// When reader is detected, app switches to MainWindow
/// </summary>
public partial class RfidScannerWindow : Window
{
    public RfidScannerWindow()
    {
        InitializeComponent();
        
        Debug.WriteLine("???????????????????????????????????????");
        Debug.WriteLine("  RFID SCANNER WINDOW (WAITING STATE)");
        Debug.WriteLine("  Showing 'Waiting for Reader' screen");
        Debug.WriteLine("???????????????????????????????????????");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("? RfidScannerWindow loaded - waiting for RFID reader");
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // Set window to cover full screen (windowed fullscreen) using WPF SystemParameters
        this.Left = 0;
        this.Top = 0;
        this.Width = SystemParameters.PrimaryScreenWidth;
        this.Height = SystemParameters.PrimaryScreenHeight;
        
        Debug.WriteLine($"? Windowed fullscreen: {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        Debug.WriteLine("?? RfidScannerWindow activated - still waiting for reader");
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Debug.WriteLine("?? RfidScannerWindow deactivated");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // No key handling needed - this is just a waiting screen
        // When reader is detected, app will switch to MainWindow
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        
        Debug.WriteLine("???????????????????????????????????????");
        Debug.WriteLine("  RFID SCANNER WINDOW CLOSED");
        Debug.WriteLine("???????????????????????????????????????");
    }
}
