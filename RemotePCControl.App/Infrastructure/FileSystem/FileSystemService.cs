#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Infrastructure.FileSystem;

public sealed class FileSystemService
{
    private bool _isDriveRedirectEnabled;

    public bool IsDriveRedirectEnabled => _isDriveRedirectEnabled;

    public void SetDriveRedirectEnabled(bool enabled)
    {
        _isDriveRedirectEnabled = enabled;
    }

    /// <summary>
    /// 로컬 드라이브 또는 특정 폴더의 파일/디렉토리 목록을 JSON 형식으로 반환합니다.
    /// </summary>
    public string GetDirectoryListingJson(string path)
    {
        if (!_isDriveRedirectEnabled)
        {
            return JsonSerializer.Serialize(new FileSystemListResponse
            {
                IsSuccess = false,
                CurrentPath = path,
                ErrorMessage = "Drive redirection is disabled."
            });
        }

        try
        {
            // 보안 정책: 초기 단계에서는 공용 라이브러리 폴더 또는 내 컴퓨터 수준의 안전한 경로 접근을 가정
            if (string.IsNullOrWhiteSpace(path))
            {
                // 드라이브 목록 반환
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new RemotePCControl.App.Models.FileEntry { Name = d.Name, IsDirectory = true, Path = d.RootDirectory.FullName })
                    .ToList();
                return JsonSerializer.Serialize(new FileSystemListResponse
                {
                    IsSuccess = true,
                    CurrentPath = string.Empty,
                    Entries = drives
                });
            }

            if (!Directory.Exists(path))
            {
                return JsonSerializer.Serialize(new FileSystemListResponse
                {
                    IsSuccess = false,
                    CurrentPath = path,
                    ErrorMessage = "Directory not found."
                });
            }

            var dirInfo = new DirectoryInfo(path);
            var entries = new List<RemotePCControl.App.Models.FileEntry>();

            foreach (var dir in dirInfo.GetDirectories())
            {
                entries.Add(new RemotePCControl.App.Models.FileEntry { Name = dir.Name, IsDirectory = true, Path = dir.FullName });
            }

            foreach (var file in dirInfo.GetFiles())
            {
                entries.Add(new RemotePCControl.App.Models.FileEntry { Name = file.Name, IsDirectory = false, Path = file.FullName, Size = file.Length });
            }

            return JsonSerializer.Serialize(new FileSystemListResponse
            {
                IsSuccess = true,
                CurrentPath = path,
                Entries = entries
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonSerializer.Serialize(new FileSystemListResponse
            {
                IsSuccess = false,
                CurrentPath = path,
                ErrorMessage = $"Access denied. {ex.Message}"
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new FileSystemListResponse
            {
                IsSuccess = false,
                CurrentPath = path,
                ErrorMessage = ex.Message
            });
        }
    }
}
