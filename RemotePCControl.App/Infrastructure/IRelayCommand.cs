using System.Windows.Input;

namespace RemotePCControl.App.Infrastructure;

public interface IRelayCommand : ICommand
{
    void NotifyCanExecuteChanged();
}
