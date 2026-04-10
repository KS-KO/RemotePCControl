#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public DevicePreferenceStore()
    {
        string baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemotePCControl");
        Directory.CreateDirectory(baseDirectory);
        _preferenceFilePath = Path.Combine(baseDirectory, "device-preferences.json");
    }

    public DevicePreferenceSnapshot Load()
    {
        if (!File.Exists(_preferenceFilePath))
        {
            return DevicePreferenceSnapshot.Empty;
        }

        string json = File.ReadAllText(_preferenceFilePath);
        PersistedDevicePreferenceSnapshot? persisted = JsonSerializer.Deserialize<PersistedDevicePreferenceSnapshot>(json, SerializerOptions);
        if (persisted is null)
        {
            return DevicePreferenceSnapshot.Empty;
        }

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
                .ToArray());
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
                .ToArray()
        };

        string json = JsonSerializer.Serialize(persisted, SerializerOptions);
        File.WriteAllText(_preferenceFilePath, json);
    }

    public sealed record DevicePreferenceSnapshot(
        IReadOnlyList<string> FavoriteDeviceInternalGuids,
        IReadOnlyList<RecentConnectionEntry> RecentConnections)
    {
        public static DevicePreferenceSnapshot Empty { get; } = new([], []);
    }

    private sealed class PersistedDevicePreferenceSnapshot
    {
        public string[]? FavoriteDeviceInternalGuids { get; init; }

        public PersistedRecentConnectionEntry[]? RecentConnections { get; init; }
    }

    private sealed class PersistedRecentConnectionEntry
    {
        public string DeviceInternalGuid { get; init; } = string.Empty;

        public string DeviceName { get; init; } = string.Empty;

        public string DeviceCode { get; init; } = string.Empty;

        public string LastApprovalMode { get; init; } = string.Empty;

        public DateTime LastConnectedAt { get; init; }
    }
}
