#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Infrastructure.Logging;

public sealed class SessionLogStore
{
    private const int MaxLogEntries = 1000;
    private readonly string _logFilePath;
    private readonly string _backupFilePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public SessionLogStore()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePCControl", "Logs");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        _logFilePath = Path.Combine(folder, "session_history.json");
        _backupFilePath = Path.Combine(folder, "session_history.corrupt.json");
    }

    public void SaveLog(SessionLogEntry entry)
    {
        lock (_lock)
        {
            try
            {
                List<SessionLogEntry> logs = LoadAllLogsInternal();
                logs.Insert(0, entry);

                logs = NormalizeLogs(logs);
                string json = JsonSerializer.Serialize(logs, SerializerOptions);
                File.WriteAllText(_logFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogStore] Failed to save log: {ex.Message}");
            }
        }
    }

    public List<SessionLogEntry> LoadAllLogs()
    {
        lock (_lock)
        {
            return LoadAllLogsInternal();
        }
    }

    private List<SessionLogEntry> LoadAllLogsInternal()
    {
        try
        {
            if (!File.Exists(_logFilePath))
            {
                return [];
            }

            string json = File.ReadAllText(_logFilePath);
            List<SessionLogEntry>? logs = JsonSerializer.Deserialize<List<SessionLogEntry>>(json, SerializerOptions);
            return NormalizeLogs(logs ?? []);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionLogStore] Failed to load logs: {ex.Message}");
            BackupCorruptedLogFile();
            return [];
        }
    }

    private void BackupCorruptedLogFile()
    {
        try
        {
            if (!File.Exists(_logFilePath))
            {
                return;
            }

            string backupPath = Path.Combine(
                Path.GetDirectoryName(_backupFilePath)!,
                $"session_history.corrupt.{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Copy(_logFilePath, backupPath, overwrite: true);
            File.Delete(_logFilePath);
            Debug.WriteLine($"[SessionLogStore] Corrupted log file backed up to: {backupPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionLogStore] Failed to backup corrupted log file: {ex.Message}");
        }
    }

    private static List<SessionLogEntry> NormalizeLogs(IEnumerable<SessionLogEntry> logs)
    {
        return logs
            .Where(log => !string.IsNullOrWhiteSpace(log.Title) && !string.IsNullOrWhiteSpace(log.Message))
            .OrderByDescending(log => log.Timestamp)
            .Take(MaxLogEntries)
            .ToList();
    }

    public void ReplaceAllLogs(IEnumerable<SessionLogEntry> logs)
    {
        lock (_lock)
        {
            try
            {
                List<SessionLogEntry> normalized = NormalizeLogs(logs);
                string json = JsonSerializer.Serialize(normalized, SerializerOptions);
                File.WriteAllText(_logFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogStore] Failed to replace logs: {ex.Message}");
            }
        }
    }
}
