#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class LocalDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 41099;
    private const int DefaultRemoteControlPort = 9999;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, DiscoveredDeviceState> _discoveredDevices = new();
    private readonly TimeSpan _offlineThreshold = TimeSpan.FromSeconds(15);
    private UdpClient? _listener;
    private CancellationTokenSource? _listenerCts;
    private CancellationTokenSource? _announceCts;
    private bool _isDisposed;

    public event Action? DevicesUpdated;

    public void Start(DeviceIdentity localIdentity)
    {
        ThrowIfDisposed();

        if (_listener is not null)
        {
            return;
        }

        _listener = new UdpClient(DiscoveryPort)
        {
            EnableBroadcast = true
        };
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        _listenerCts = new CancellationTokenSource();
        _announceCts = new CancellationTokenSource();

        _ = ListenAsync(localIdentity, _listenerCts.Token);
        _ = AnnounceLoopAsync(localIdentity, _announceCts.Token);
    }

    public async Task<DuplicateCheckResult> CheckDuplicateAsync(DeviceIdentity localIdentity, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        using UdpClient probeClient = new(0)
        {
            EnableBroadcast = true
        };

        IPEndPoint localEndPoint = (IPEndPoint)probeClient.Client.LocalEndPoint!;
        DiscoveryEnvelope probeEnvelope = DiscoveryEnvelope.CreateProbe(localIdentity, localEndPoint.Port);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(probeEnvelope, SerializerOptions));
        await probeClient.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)).ConfigureAwait(false);

        List<DeviceModel> conflicts = [];
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(1200);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            Task<UdpReceiveResult> receiveTask = probeClient.ReceiveAsync(cancellationToken).AsTask();
            Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
            
            if (completedTask != receiveTask)
            {
                // Timeout or cancellation occurred before receiving data.
                // The receiveTask is still pending. We should ensure its exception is observed if it eventually fails.
                _ = receiveTask.ContinueWith(t => 
                {
                    if (t.IsFaulted) { var _ = t.Exception; } // Observe exception
                }, TaskContinuationOptions.OnlyOnFaulted);
                break;
            }

            UdpReceiveResult receiveResult = await receiveTask.ConfigureAwait(false);
            DiscoveryEnvelope? envelope = TryDeserialize(receiveResult.Buffer);
            if (envelope is null || envelope.MessageType != DiscoveryMessageType.ProbeReply)
            {
                continue;
            }

            if (string.Equals(envelope.InternalGuid, localIdentity.InternalGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool hasDuplicateName = string.Equals(envelope.DeviceName, localIdentity.DeviceName, StringComparison.OrdinalIgnoreCase);
            bool hasDuplicateCode = string.Equals(envelope.DeviceCode, localIdentity.DeviceCode, StringComparison.OrdinalIgnoreCase);
            if (!hasDuplicateName && !hasDuplicateCode)
            {
                continue;
            }

            conflicts.Add(CreateDeviceModel(envelope, receiveResult.RemoteEndPoint.Address));
        }

        return conflicts.Count == 0
            ? DuplicateCheckResult.None
            : new DuplicateCheckResult
            {
                IsDuplicate = true,
                Conflicts = conflicts
            };
    }

    public IReadOnlyList<DeviceModel> GetDiscoveredDevices()
    {
        DateTime utcNow = DateTime.UtcNow;
        return _discoveredDevices.Values
            .Select(state => CreateDeviceModel(state.Envelope, state.RemoteAddress, utcNow - state.LastSeenUtc))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _listenerCts?.Cancel();
        _announceCts?.Cancel();
        _listener?.Dispose();
        _listenerCts?.Dispose();
        _announceCts?.Dispose();
    }

    private async Task ListenAsync(DeviceIdentity localIdentity, CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult receiveResult = await _listener.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                DiscoveryEnvelope? envelope = TryDeserialize(receiveResult.Buffer);
                if (envelope is null)
                {
                    continue;
                }

                if (string.Equals(envelope.InternalGuid, localIdentity.InternalGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (envelope.MessageType == DiscoveryMessageType.Announce)
                {
                    UpdateDiscoveredDevice(envelope, receiveResult.RemoteEndPoint.Address);
                }
                else if (envelope.MessageType == DiscoveryMessageType.Probe)
                {
                    UpdateDiscoveredDevice(envelope, receiveResult.RemoteEndPoint.Address);
                    await SendProbeReplyAsync(localIdentity, receiveResult.RemoteEndPoint.Address, envelope.ReplyPort).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discovery] Listen loop error: {ex.Message}");
        }
    }

    private async Task AnnounceLoopAsync(DeviceIdentity localIdentity, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendAnnounceAsync(localIdentity, cancellationToken).ConfigureAwait(false);
                PruneOfflineDevices();
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discovery] Announce loop error: {ex.Message}");
        }
    }

    private async Task SendAnnounceAsync(DeviceIdentity localIdentity, CancellationToken cancellationToken)
    {
        using UdpClient announceClient = new(0)
        {
            EnableBroadcast = true
        };

        DiscoveryEnvelope announceEnvelope = DiscoveryEnvelope.CreateAnnounce(localIdentity);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announceEnvelope, SerializerOptions));
        await announceClient.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task SendProbeReplyAsync(DeviceIdentity localIdentity, IPAddress remoteAddress, int replyPort)
    {
        if (replyPort <= 0)
        {
            return;
        }

        using UdpClient replyClient = new(0);
        DiscoveryEnvelope replyEnvelope = DiscoveryEnvelope.CreateProbeReply(localIdentity);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replyEnvelope, SerializerOptions));
        await replyClient.SendAsync(payload, payload.Length, new IPEndPoint(remoteAddress, replyPort)).ConfigureAwait(false);
    }

    private void UpdateDiscoveredDevice(DiscoveryEnvelope envelope, IPAddress remoteAddress)
    {
        _discoveredDevices.AddOrUpdate(
            envelope.InternalGuid,
            _ => new DiscoveredDeviceState(envelope, remoteAddress, DateTime.UtcNow),
            (_, _) => new DiscoveredDeviceState(envelope, remoteAddress, DateTime.UtcNow));
        DevicesUpdated?.Invoke();
    }

    private void PruneOfflineDevices()
    {
        DateTime utcNow = DateTime.UtcNow;
        bool changed = false;

        foreach ((string key, DiscoveredDeviceState value) in _discoveredDevices)
        {
            if (utcNow - value.LastSeenUtc <= _offlineThreshold)
            {
                continue;
            }

            changed |= _discoveredDevices.TryRemove(key, out _);
        }

        if (changed)
        {
            DevicesUpdated?.Invoke();
        }
    }

    private static DeviceModel CreateDeviceModel(DiscoveryEnvelope envelope, IPAddress remoteAddress, TimeSpan? age = null)
    {
        string lastSeenLabel = age is null
            ? "Last seen: just now"
            : age.Value < TimeSpan.FromSeconds(10)
                ? "Last seen: just now"
                : $"Last seen: {Math.Max(1, (int)age.Value.TotalSeconds)} seconds ago";

        return new DeviceModel
        {
            Name = envelope.DeviceName,
            DeviceId = envelope.DeviceCode,
            DeviceCode = envelope.DeviceCode,
            InternalGuid = envelope.InternalGuid,
            Description = $"Discovered on local network via UDP broadcast ({remoteAddress}).",
            LastSeenLabel = lastSeenLabel,
            Status = DeviceStatus.Online,
            IsFavorite = false,
            Endpoints =
            [
                new DeviceEndpoint
                {
                    Address = remoteAddress.ToString(),
                    Port = envelope.RemoteControlPort,
                    Scope = DeviceEndpointScope.Local
                }
            ],
            Capabilities = ["Screen", "Input", "Broadcast Discovery"]
        };
    }

    private static DiscoveryEnvelope? TryDeserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<DiscoveryEnvelope>(payload, SerializerOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discovery] Payload deserialize error: {ex.Message}");
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(LocalDiscoveryService));
        }
    }

    private sealed record DiscoveredDeviceState(DiscoveryEnvelope Envelope, IPAddress RemoteAddress, DateTime LastSeenUtc);

    private enum DiscoveryMessageType
    {
        Announce,
        Probe,
        ProbeReply
    }

    private sealed class DiscoveryEnvelope
    {
        public required DiscoveryMessageType MessageType { get; init; }

        public required string InternalGuid { get; init; }

        public required string DeviceName { get; init; }

        public required string DeviceCode { get; init; }

        public required int RemoteControlPort { get; init; }

        public int ReplyPort { get; init; }

        public static DiscoveryEnvelope CreateAnnounce(DeviceIdentity identity)
        {
            return new DiscoveryEnvelope
            {
                MessageType = DiscoveryMessageType.Announce,
                InternalGuid = identity.InternalGuid,
                DeviceName = identity.DeviceName,
                DeviceCode = identity.DeviceCode,
                RemoteControlPort = DefaultRemoteControlPort,
                ReplyPort = 0
            };
        }

        public static DiscoveryEnvelope CreateProbe(DeviceIdentity identity, int replyPort)
        {
            return new DiscoveryEnvelope
            {
                MessageType = DiscoveryMessageType.Probe,
                InternalGuid = identity.InternalGuid,
                DeviceName = identity.DeviceName,
                DeviceCode = identity.DeviceCode,
                RemoteControlPort = DefaultRemoteControlPort,
                ReplyPort = replyPort
            };
        }

        public static DiscoveryEnvelope CreateProbeReply(DeviceIdentity identity)
        {
            return new DiscoveryEnvelope
            {
                MessageType = DiscoveryMessageType.ProbeReply,
                InternalGuid = identity.InternalGuid,
                DeviceName = identity.DeviceName,
                DeviceCode = identity.DeviceCode,
                RemoteControlPort = DefaultRemoteControlPort,
                ReplyPort = 0
            };
        }
    }
}
