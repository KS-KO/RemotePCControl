using System.Windows.Media;
using RemotePCControl.App.Infrastructure;

namespace RemotePCControl.App.Models;

public sealed class DeviceModel : ObservableObject
{
    private DeviceStatus _status;
    private bool _isFavorite;
    private string _lastSeenLabel = "Last seen: unknown";

    public required string Name { get; init; }

    public required string DeviceId { get; init; }

    public required string DeviceCode { get; init; }

    public required string InternalGuid { get; init; }

    public required string Description { get; init; }

    public required List<DeviceEndpoint> Endpoints { get; init; }

    public string LastSeenLabel
    {
        get => _lastSeenLabel;
        set => SetProperty(ref _lastSeenLabel, value);
    }

    public required List<string> Capabilities { get; init; }

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

    public Brush StatusBrush =>
        Status switch
        {
            DeviceStatus.Online => new SolidColorBrush(Color.FromRgb(14, 159, 110)),
            DeviceStatus.Busy => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            _ => new SolidColorBrush(Color.FromRgb(115, 127, 148))
        };
}
