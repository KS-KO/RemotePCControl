#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class TcpConnectionManager : IDisposable
{
    // 스레드 안전성 확보를 위한 ConcurrentDictionary 사용
    private readonly ConcurrentDictionary<string, TcpSession> _activeSessions = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _listeningCts;
    private bool _isDisposed;

    public event Action<TcpSession>? OnSessionConnected;
    public event Action<TcpSession, Exception?>? OnSessionDisconnected;

    public void StartListening(int port)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(TcpConnectionManager));
        if (_listener is not null) return;

        _listeningCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        Debug.WriteLine($"[Network] Started listening for connections on port {port}.");
        // 백그라운드 태스크로 연결 수락 대기루프 실행, ConfigureAwait(false) 처리
        _ = AcceptConnectionsAsync(_listeningCts.Token);
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener is not null)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var session = new TcpSession(client);
                RegisterSession(session);
            }
        }
        catch (OperationCanceledException)
        {
            // 의도된 취소 무시
        }
        catch (Exception ex)
        {
            // 상태코드/오류 메시지 처리
            Debug.WriteLine($"[Network] Accept Loop Connection Request Error: {ex.Message}");
        }
    }

    public async Task<TcpSession> ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(TcpConnectionManager));

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(ipAddress, port, cancellationToken).ConfigureAwait(false);
            var session = new TcpSession(client);
            RegisterSession(session);
            return session;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Network] Connection failed to {ipAddress}:{port}. Error: {ex.Message}");
            client.Dispose();
            throw;
        }
    }

    private void RegisterSession(TcpSession session)
    {
        if (_activeSessions.TryAdd(session.SessionId, session))
        {
            session.OnDisconnected += HandleSessionDisconnected;
            OnSessionConnected?.Invoke(session);
            session.StartReceiving();
        }
    }

    private void HandleSessionDisconnected(TcpSession session, Exception? ex)
    {
        if (_activeSessions.TryRemove(session.SessionId, out _))
        {
            session.OnDisconnected -= HandleSessionDisconnected;
            OnSessionDisconnected?.Invoke(session, ex);
        }
    }

    public void Dispose()
    {
        // IDisposable 강제, 외부 통신 객체 해제
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _listeningCts?.Cancel();
            _listener?.Stop();

            foreach (var session in _activeSessions.Values)
            {
                // 각 세션 리소스 반납
                session.Dispose();
            }
            _activeSessions.Clear();
            _listeningCts?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Network] Dispose Error: {ex.Message}");
        }
    }
}
