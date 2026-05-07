using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace RAMSOfficial;

public partial class ManualIdInputDialog : Window
{
    public string? EnteredId { get; private set; }
    public bool WasSubmitted { get; private set; }

    public ManualIdInputDialog()
    {
        InitializeComponent();
        
        // Auto-focus on input
        Loaded += (s, e) => IdInputTextBox.Focus();

        DataObject.AddPastingHandler(IdInputTextBox, OnIdInputTextBoxPaste);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter = Submit
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SubmitId();
        }
        // Escape = Cancel
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelDialog();
        }
    }

    private void IdInputTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        IdInputTextBox.SelectAll();
    }

    private void IdInputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var original = IdInputTextBox.Text ?? string.Empty;
        var sanitized = new string(original.Where(char.IsDigit).Take(10).ToArray());

        if (!string.Equals(original, sanitized, StringComparison.Ordinal))
        {
            var caret = IdInputTextBox.CaretIndex;
            IdInputTextBox.Text = sanitized;
            IdInputTextBox.CaretIndex = Math.Min(caret, sanitized.Length);
        }

        CharCountTextBlock.Text = $"{sanitized.Length} / 10";
    }

    private void IdInputTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void OnIdInputTextBoxPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pasted = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;
        if (!pasted.All(char.IsDigit))
        {
            e.CancelCommand();
        }
    }

    private void SubmitId()
    {
        var id = IdInputTextBox.Text?.Trim();
        
        if (string.IsNullOrWhiteSpace(id))
        {
            MessageBox.Show(
                "Please enter an RFID code before submitting.\n\nThe input field cannot be empty.",
                "Empty Input",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            IdInputTextBox.Focus();
            return;
        }

        EnteredId = id;
        WasSubmitted = true;
        DialogResult = true;
        Close();
    }

    private void CancelDialog()
    {
        WasSubmitted = false;
        DialogResult = false;
        Close();
    }
}
