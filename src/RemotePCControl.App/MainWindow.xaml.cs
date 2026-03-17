using System.Windows;
using RemotePCControl.App.Services;
using RemotePCControl.App.ViewModels;

namespace RemotePCControl.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new MockRemoteSessionService());
    }
}
