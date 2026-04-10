#nullable enable
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class FileTransferService
{
    // 대용량 처리를 위한 1MB 단위 할당 사이즈 정의
    private const int CHUNK_SIZE = 1024 * 1024;
    public event Action<string, double>? ProgressChanged;

    /// <summary>
    /// 파일 이름 정보를 먼저 전송한 후 데이터를 전송하는 고성능 송신 파이프라인
    /// </summary>
    public async Task SendFileWithMetaAsync(string filePath, TcpSession session, byte metaType, byte chunkType, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        string fileName = Path.GetFileName(filePath);
        byte[] fileNameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        
        // 1. 메타데이터 전송 (유형 + 파일명 길이 + 파일명 + 파일 크기)
        byte[] metaPacket = new byte[1 + 4 + fileNameBytes.Length + 8];
        metaPacket[0] = metaType;
        BitConverter.TryWriteBytes(metaPacket.AsSpan(1, 4), fileNameBytes.Length);
        fileNameBytes.CopyTo(metaPacket.AsSpan(5));
        
        long fileSize = new FileInfo(filePath).Length;
        BitConverter.TryWriteBytes(metaPacket.AsSpan(5 + fileNameBytes.Length, 8), fileSize);
        
        await session.SendAsync(metaPacket, cancellationToken).ConfigureAwait(false);

        // 2. 파일 데이터 전송
        // LOH 단편화 방지: FileStream 읽기용 공유 버퍼 풀 사용
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CHUNK_SIZE, useAsync: true);
            
            int bytesRead;
            long totalRead = 0;
            while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(1, CHUNK_SIZE - 1), cancellationToken).ConfigureAwait(false)) > 0)
            {
                // 패킷 헤더 추가
                buffer[0] = chunkType;
                await session.SendAsync(buffer.AsMemory(0, bytesRead + 1), cancellationToken).ConfigureAwait(false);
                
                totalRead += bytesRead;
                ProgressChanged?.Invoke(filePath, (double)totalRead * 100 / fileSize);
            }
            ProgressChanged?.Invoke(filePath, 100.0);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[FileTransfer] Transmitting canceled: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileTransfer] SendFileWithMetaAsync Error: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// FileStream의 useAsync 옵션과 ArrayPool을 조합한 고성능 파일 전송 송신 파이프라인 (단순 청크 전송)
    /// </summary>
    public async Task SendFileAsync(string filePath, TcpSession session, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        // LOH 단편화 방지: FileStream 읽기용 공유 버퍼 풀 사용
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CHUNK_SIZE, useAsync: true);
            
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(1, CHUNK_SIZE - 1), cancellationToken).ConfigureAwait(false)) > 0)
            {
                // 패킷 헤더 추가 (0x03 = 파일 청크 시작점)
                buffer[0] = 0x03;
                await session.SendAsync(buffer.AsMemory(0, bytesRead + 1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[FileTransfer] Transmitting canceled: {filePath}");
        }
        catch (Exception ex)
        {
            // 방어적 예외 로깅
            Debug.WriteLine($"[FileTransfer] SendFileAsync Error: {ex.Message}");
        }
        finally
        {
            // 버퍼 안전 반환
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 수신한 byte 배열 청크를 디스크 파일에 Append 모드로 병합 저장하는 수신 파이프라인
    /// </summary>
    public async Task ReceiveFileChunkAsync(string destinationPath, ReadOnlyMemory<byte> dataChunk, CancellationToken cancellationToken)
    {
        try
        {
            // 디렉토리 존재 확인 및 생성
            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Windows 커널 단 비동기 I/O를 적극 활용하도록 useAsync: true 사용
            using var fs = new FileStream(destinationPath, FileMode.Append, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            
            await fs.WriteAsync(dataChunk, cancellationToken).ConfigureAwait(false);
            
            // 데이터 무결성을 위해 즉시 Flush
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileTransfer] ReceiveFileChunkAsync Error: {ex.Message}");
        }
    }
}
