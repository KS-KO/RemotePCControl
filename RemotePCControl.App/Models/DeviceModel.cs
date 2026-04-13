using System.Windows.Media;
using RemotePCControl.App.Infrastructure;

namespace RemotePCControl.App.Models;

public sealed class DeviceModel : ObservableObject
{
    private DeviceStatus _status;
    private bool _isFavorite;
    private string _lastSeenLabel = "Last seen: unknown";

    private string _name = string.Empty;
    private string _description = string.Empty;
    private string? _trustedThumbprint;

    public string? TrustedThumbprint
    {
        get => _trustedThumbprint;
        set => SetProperty(ref _trustedThumbprint, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string DeviceId { get; init; } = string.Empty;

    public string DeviceCode { get; init; } = string.Empty;

    public string InternalGuid { get; init; } = Guid.NewGuid().ToString();

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public List<DeviceEndpoint> Endpoints { get; init; } = [];

    public string LastSeenLabel
    {
        get => _lastSeenLabel;
        set => SetProperty(ref _lastSeenLabel, value);
    }

    public List<string> Capabilities { get; init; } = [];

    public DeviceStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                RaisePropertyChanged(nameof(StatusLabel));
                RaisePropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    public string StatusLabel =>
        Status switch
        {
            DeviceStatus.Online => "Online",
            DeviceStatus.Busy => "Busy",
            _ => "Offline"
        };

    public System.Windows.Media.Brush StatusBrush =>
        Status switch
        {
            DeviceStatus.Online => new SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 159, 110)),
            DeviceStatus.Busy => new SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 119, 6)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(115, 127, 148))
        };
}
