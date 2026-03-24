#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class TcpSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1); // 스레드 안전성 확보
    private bool _isDisposed;
    
    // 메시지 수신 이벤트 (UI 또는 서비스 레이어 연동용, Zero Allocation 접근)
    public event Action<TcpSession, ReadOnlyMemory<byte>>? OnMessageReceived;
    public event Action<TcpSession, Exception?>? OnDisconnected;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public TcpSession(TcpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.NoDelay = true; // 입력 지연 방지 (NFR-3)
        _stream = _client.GetStream();
    }

    public void StartReceiving()
    {
        _ = ReceiveLoopAsync(_cts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // 4바이트 헤더(페이로드 길이) 수신용 버퍼 (GC 압박 방지)
        var lengthBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int headerRead = await ReadExactAsync(_stream, lengthBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                if (headerRead == 0) break; // 클라이언트 연결 종료

                int payloadLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (payloadLength <= 0 || payloadLength > 50 * 1024 * 1024) 
                {
                    // 비정상적인 버퍼 크기 방지 (최대 50MB 제한)
                    throw new InvalidOperationException($"Invalid payload length: {payloadLength}");
                }

                // 대용량 전송 데이터(화면 프레임, 텍스트)를 위해 ArrayPool 사용 (LOH 단편화 방지)
                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                try
                {
                    int payloadRead = await ReadExactAsync(_stream, payloadBuffer.AsMemory(0, payloadLength), cancellationToken).ConfigureAwait(false);
                    if (payloadRead == 0) break;

                    // 외부 처리를 위한 데이터 복사는 사용처에서 직접 수행하거나, ReadOnlyMemory를 그대로 넘김(Zero Allocation 방침)
                    OnMessageReceived?.Invoke(this, new ReadOnlyMemory<byte>(payloadBuffer, 0, payloadLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 의도된 취소
        }
        catch (Exception ex)
        {
            // 빈 catch 블록 절대 금지 원칙 - 구조화된 로깅 고려
            Debug.WriteLine($"[TcpSession] Session {SessionId} receive loop exception: {ex.Message}");
            OnDisconnected?.Invoke(this, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthBuffer);
            if (!_isDisposed)
            {
                OnDisconnected?.Invoke(this, null);
                Dispose();
            }
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(TcpSession));

        var lengthBuffer = BitConverter.GetBytes(payload.Length);

        // NetworkStream은 동시 Write 시 스레드 충돌 가능성이 있으므로 SemaphoreSlim 사용
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TcpSession] Send Exception: {ex.Message}");
            Dispose();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        int target = buffer.Length;
        while (totalRead < target)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return 0; // EOF
            totalRead += read;
        }
        return totalRead;
    }

    public void Dispose()
    {
        // IDisposable 강제, 중복 해제 방지
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _cts.Cancel();
            _stream.Dispose();
            _client.Dispose();
            _cts.Dispose();
            _writeLock.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TcpSession] Dispose Exception: {ex.Message}");
        }
    }
}
