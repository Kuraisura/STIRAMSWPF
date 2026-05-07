using System.Windows;
using System.Windows.Input;

namespace RAMSOfficial;

public partial class DeveloperConsoleAuthWindow : Window
{
    private readonly string _expectedPassword;
    private readonly string _accessName;

    public bool IsAuthenticated { get; private set; }

    public DeveloperConsoleAuthWindow(string expectedPassword)
        : this(expectedPassword, "Developer Console")
    {
    }

    public DeveloperConsoleAuthWindow(string expectedPassword, string accessName)
    {
        InitializeComponent();
        _expectedPassword = expectedPassword;
        _accessName = string.IsNullOrWhiteSpace(accessName) ? "Restricted Area" : accessName;

        Title = $"{_accessName} Access";
        TitleText.Text = _accessName;
        SubtitleText.Text = "Enter password to continue";
        UnlockButton.Content = "Unlock";

        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        ValidatePassword();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ValidatePassword();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void ValidatePassword()
    {
        if (PasswordInput.Password == _expectedPassword)
        {
            IsAuthenticated = true;
            DialogResult = true;
            Close();
            return;
        }

        IsAuthenticated = false;
        ErrorText.Visibility = Visibility.Visible;
        MessageBox.Show(
            "Incorrect password.\n\n" +
            $"Access to {_accessName} is restricted.\n" +
            "Please contact MIS Personnel if you need authorized access.",
            "Access Denied",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        PasswordInput.Clear();
        PasswordInput.Focus();
    }
}
