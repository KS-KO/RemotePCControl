using System.Windows;

namespace RemotePCControl.App.Views;

/// <summary>
/// Interaction logic for ExitConfirmDialog.xaml
/// </summary>
public partial class ExitConfirmDialog : Window
{
    public ExitConfirmDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
