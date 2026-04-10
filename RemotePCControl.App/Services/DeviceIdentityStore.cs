#nullable enable
using System;
using System.IO;
using System.Text.Json;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class DeviceIdentityStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _identityFilePath;

    public DeviceIdentityStore()
    {
        string baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemotePCControl");
        Directory.CreateDirectory(baseDirectory);
        _identityFilePath = Path.Combine(baseDirectory, "device-identity.json");
    }

    public DeviceIdentity LoadOrCreate()
    {
        if (File.Exists(_identityFilePath))
        {
            string json = File.ReadAllText(_identityFilePath);
            DeviceIdentity? existingIdentity = JsonSerializer.Deserialize<DeviceIdentity>(json, SerializerOptions);
            if (existingIdentity is not null)
            {
                return existingIdentity;
            }
        }

        DeviceIdentity createdIdentity = new()
        {
            InternalGuid = Guid.NewGuid().ToString("N"),
            DeviceName = $"RemotePC-{Environment.MachineName}",
            DeviceCode = $"RPC-{Environment.MachineName.ToUpperInvariant()}"
        };

        Save(createdIdentity);
        return createdIdentity;
    }

    public void Save(DeviceIdentity identity)
    {
        string json = JsonSerializer.Serialize(identity, SerializerOptions);
        File.WriteAllText(_identityFilePath, json);
    }
}
