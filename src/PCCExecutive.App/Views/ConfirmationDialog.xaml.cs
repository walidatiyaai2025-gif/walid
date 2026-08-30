using System.Windows;

namespace PCCExecutive.App.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(string title, string message, string confirmLabel)
    {
        InitializeComponent();
        DataContext = new { Title = title, Message = message, ConfirmLabel = confirmLabel };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
