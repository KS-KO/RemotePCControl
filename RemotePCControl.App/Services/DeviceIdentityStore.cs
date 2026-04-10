using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    // 엔트로피는 무단 암호 해독을 좀 더 어렵게 하기 위한 추가 소금(Salt) 역할
    private static readonly byte[] Entropy = "RemotePCControl_Identity_Salt"u8.ToArray();

    public DeviceIdentityStore()
    {
        string baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemotePCControl");
        Directory.CreateDirectory(baseDirectory);
        _identityFilePath = Path.Combine(baseDirectory, "device-identity.dat"); // 암호화된 파일임을 명시하기 위해 확장자 변경
    }

    public DeviceIdentity LoadOrCreate()
    {
        if (File.Exists(_identityFilePath))
        {
            try
            {
                byte[] encryptedData = File.ReadAllBytes(_identityFilePath);
                byte[] decryptedData = ProtectedData.Unprotect(encryptedData, Entropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(decryptedData);
                
                DeviceIdentity? existingIdentity = JsonSerializer.Deserialize<DeviceIdentity>(json, SerializerOptions);
                if (existingIdentity is not null)
                {
                    return existingIdentity;
                }
            }
            catch (Exception ex)
            {
                // 복호화 실패 시(사용자가 바뀌었거나 데이터 오염 등) 새로 생성
                System.Diagnostics.Debug.WriteLine($"[DeviceIdentityStore] Load/Decrypt failed: {ex.Message}");
            }
        }

        // 구버전 파일(json)이 존재하면 마이그레이션 시도
        string oldPath = Path.Combine(Path.GetDirectoryName(_identityFilePath)!, "device-identity.json");
        if (File.Exists(oldPath))
        {
            try
            {
                string oldJson = File.ReadAllText(oldPath);
                DeviceIdentity? oldIdentity = JsonSerializer.Deserialize<DeviceIdentity>(oldJson, SerializerOptions);
                if (oldIdentity is not null)
                {
                    Save(oldIdentity);
                    File.Delete(oldPath);
                    return oldIdentity;
                }
            }
            catch { }
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
        byte[] dataToProtect = Encoding.UTF8.GetBytes(json);
        byte[] encryptedData = ProtectedData.Protect(dataToProtect, Entropy, DataProtectionScope.CurrentUser);
        
        File.WriteAllBytes(_identityFilePath, encryptedData);
    }
}
