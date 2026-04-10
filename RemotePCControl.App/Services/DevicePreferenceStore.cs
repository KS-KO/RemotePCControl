using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class DevicePreferenceStore
{
    private const int MaxRecentConnections = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _preferenceFilePath;
    private static readonly byte[] Entropy = "RemotePCControl_Preference_Salt"u8.ToArray();

    public DevicePreferenceStore()
    {
        string baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemotePCControl");
        Directory.CreateDirectory(baseDirectory);
        _preferenceFilePath = Path.Combine(baseDirectory, "device-preferences.dat");
    }

    public DevicePreferenceSnapshot Load()
    {
        if (File.Exists(_preferenceFilePath))
        {
            try
            {
                byte[] encryptedData = File.ReadAllBytes(_preferenceFilePath);
                byte[] decryptedData = ProtectedData.Unprotect(encryptedData, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(decryptedData);

                PersistedDevicePreferenceSnapshot? persisted = JsonSerializer.Deserialize<PersistedDevicePreferenceSnapshot>(json, SerializerOptions);
                if (persisted is not null)
                {
                    return ConvertFromPersisted(persisted);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevicePreferenceStore] Load failed: {ex.Message}");
            }
        }

        // 마이그레이션 시도
        string oldPath = Path.Combine(Path.GetDirectoryName(_preferenceFilePath)!, "device-preferences.json");
        if (File.Exists(oldPath))
        {
            try
            {
                string oldJson = File.ReadAllText(oldPath);
                PersistedDevicePreferenceSnapshot? oldPersisted = JsonSerializer.Deserialize<PersistedDevicePreferenceSnapshot>(oldJson, SerializerOptions);
                if (oldPersisted is not null)
                {
                    var snapshot = ConvertFromPersisted(oldPersisted);
                    Save(snapshot);
                    File.Delete(oldPath);
                    return snapshot;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevicePreferenceStore] Migration failed: {ex.Message}");
            }
        }

        return DevicePreferenceSnapshot.Empty;
    }

    public void Save(DevicePreferenceSnapshot snapshot)
    {
        PersistedDevicePreferenceSnapshot persisted = new()
        {
            FavoriteDeviceInternalGuids = snapshot.FavoriteDeviceInternalGuids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RecentConnections = snapshot.RecentConnections
                .OrderByDescending(entry => entry.LastConnectedAt)
                .Take(MaxRecentConnections)
                .Select(entry => new PersistedRecentConnectionEntry
                {
                    DeviceInternalGuid = entry.DeviceInternalGuid,
                    DeviceName = entry.DeviceName,
                    DeviceCode = entry.DeviceCode,
                    LastApprovalMode = entry.LastApprovalMode,
                    LastConnectedAt = entry.LastConnectedAt
                })
                .ToArray(),
            DeviceMetadata = snapshot.DeviceMetadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            LastKnownConnection = snapshot.LastKnownConnection is null
                ? null
                : new PersistedLastKnownConnection
                {
                    DeviceInternalGuid = snapshot.LastKnownConnection.DeviceInternalGuid,
                    DeviceName = snapshot.LastKnownConnection.DeviceName,
                    DeviceCode = snapshot.LastKnownConnection.DeviceCode,
                    LastApprovalMode = snapshot.LastKnownConnection.LastApprovalMode,
                    Address = snapshot.LastKnownConnection.Address,
                    Port = snapshot.LastKnownConnection.Port,
                    LastConnectedAt = snapshot.LastKnownConnection.LastConnectedAt
                }
        };

        string json = JsonSerializer.Serialize(persisted, SerializerOptions);
        byte[] dataToProtect = Encoding.UTF8.GetBytes(json);
        byte[] encryptedData = ProtectedData.Protect(dataToProtect, Entropy, DataProtectionScope.CurrentUser);
        
        File.WriteAllBytes(_preferenceFilePath, encryptedData);
    }

    private static DevicePreferenceSnapshot ConvertFromPersisted(PersistedDevicePreferenceSnapshot persisted)
    {
        return new DevicePreferenceSnapshot(
            persisted.FavoriteDeviceInternalGuids ?? [],
            (persisted.RecentConnections ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.DeviceInternalGuid))
                .OrderByDescending(entry => entry.LastConnectedAt)
                .Select(entry => new RecentConnectionEntry
                {
                    DeviceInternalGuid = entry.DeviceInternalGuid,
                    DeviceName = entry.DeviceName,
                    DeviceCode = entry.DeviceCode,
                    LastApprovalMode = entry.LastApprovalMode,
                    LastConnectedAt = entry.LastConnectedAt
                })
                .ToArray(),
            persisted.DeviceMetadata ?? new Dictionary<string, DeviceMetadata>(StringComparer.OrdinalIgnoreCase),
            persisted.LastKnownConnection is null
                ? null
                : new LastKnownConnectionInfo(
                    persisted.LastKnownConnection.DeviceInternalGuid,
                    persisted.LastKnownConnection.DeviceName,
                    persisted.LastKnownConnection.DeviceCode,
                    persisted.LastKnownConnection.LastApprovalMode,
                    persisted.LastKnownConnection.Address,
                    persisted.LastKnownConnection.Port,
                    persisted.LastKnownConnection.LastConnectedAt));
    }

    public sealed record DevicePreferenceSnapshot(
        IReadOnlyList<string> FavoriteDeviceInternalGuids,
        IReadOnlyList<RecentConnectionEntry> RecentConnections,
        IReadOnlyDictionary<string, DeviceMetadata> DeviceMetadata,
        LastKnownConnectionInfo? LastKnownConnection)
    {
        public static DevicePreferenceSnapshot Empty { get; } = new([], [], new Dictionary<string, DeviceMetadata>(), null);
    }

    public sealed record DeviceMetadata(string? CustomName, string? CustomDescription, string? TrustedThumbprint = null);

    public sealed record LastKnownConnectionInfo(
        string DeviceInternalGuid,
        string DeviceName,
        string DeviceCode,
        string LastApprovalMode,
        string Address,
        int Port,
        DateTime LastConnectedAt);

    private sealed class PersistedDevicePreferenceSnapshot
    {
        public string[]? FavoriteDeviceInternalGuids { get; init; }

        public PersistedRecentConnectionEntry[]? RecentConnections { get; init; }
        
        public Dictionary<string, DeviceMetadata>? DeviceMetadata { get; init; }

        public PersistedLastKnownConnection? LastKnownConnection { get; init; }
    }

    private sealed class PersistedRecentConnectionEntry
    {
        public string DeviceInternalGuid { get; init; } = string.Empty;

        public string DeviceName { get; init; } = string.Empty;

        public string DeviceCode { get; init; } = string.Empty;

        public string LastApprovalMode { get; init; } = string.Empty;

        public DateTime LastConnectedAt { get; init; }
    }

    private sealed class PersistedLastKnownConnection
    {
        public string DeviceInternalGuid { get; init; } = string.Empty;

        public string DeviceName { get; init; } = string.Empty;

        public string DeviceCode { get; init; } = string.Empty;

        public string LastApprovalMode { get; init; } = string.Empty;

        public string Address { get; init; } = string.Empty;

        public int Port { get; init; }

        public DateTime LastConnectedAt { get; init; }
    }

    public bool IsFavoriteDevice(string? internalGuid)
    {
        if (string.IsNullOrWhiteSpace(internalGuid))
        {
            return false;
        }

        DevicePreferenceSnapshot snapshot = Load();
        return snapshot.FavoriteDeviceInternalGuids.Any(value => string.Equals(value, internalGuid, StringComparison.OrdinalIgnoreCase));
    }

    public void UpdateDeviceTrustedThumbprint(string internalGuid, string thumbprint)
    {
        var existing = Load();
        var newMetaMap = existing.DeviceMetadata.ToDictionary(k => k.Key, v => v.Value);
        
        if (newMetaMap.TryGetValue(internalGuid, out var meta))
        {
            newMetaMap[internalGuid] = meta with { TrustedThumbprint = thumbprint };
        }
        else
        {
            newMetaMap[internalGuid] = new DeviceMetadata(null, null, thumbprint);
        }

        Save(existing with { DeviceMetadata = newMetaMap });
    }
}
