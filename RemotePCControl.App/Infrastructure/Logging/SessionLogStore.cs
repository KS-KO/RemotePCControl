#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Infrastructure.Logging;

public sealed class SessionLogStore
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public SessionLogStore()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePCControl", "Logs");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        _logFilePath = Path.Combine(folder, "session_history.json");
    }

    public void SaveLog(SessionLogEntry entry)
    {
        lock (_lock)
        {
            try
            {
                List<SessionLogEntry> logs = LoadAllLogs();
                logs.Insert(0, entry);
                
                // 최근 1000개만 유지 (성능 및 용량 관리)
                if (logs.Count > 1000)
                {
                    logs = logs.Take(1000).ToList();
                }

                string json = JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_logFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionLogStore] Failed to save log: {ex.Message}");
            }
        }
    }

    public List<SessionLogEntry> LoadAllLogs()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_logFilePath))
                {
                    return new List<SessionLogEntry>();
                }

                string json = File.ReadAllText(_logFilePath);
                return JsonSerializer.Deserialize<List<SessionLogEntry>>(json) ?? new List<SessionLogEntry>();
            }
            catch
            {
                return new List<SessionLogEntry>();
            }
        }
    }
}
