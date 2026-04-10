#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemotePCControl.App.Infrastructure.Security;

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
                _ = HandleNewConnectionAsync(client, cancellationToken);
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

    private async Task HandleNewConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var sslStream = new SslStream(client.GetStream(), false);
            var serverCert = CertificateManager.GetOrCreateSelfSignedCertificate();
            
            await sslStream.AuthenticateAsServerAsync(serverCert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false).ConfigureAwait(false);
            
            var session = new TcpSession(client, sslStream);
            RegisterSession(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Network] SSL Authentication failed (Server): {ex.Message}");
            client.Dispose();
        }
    }

    public async Task<TcpSession> ConnectAsync(string ipAddress, int port, string? expectedThumbprint = null, CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(TcpConnectionManager));

        var client = new TcpClient();

        try
        {
            await client.ConnectAsync(ipAddress, port, cancellationToken).ConfigureAwait(false);

            var sslStream = new SslStream(client.GetStream(), false,
                new RemoteCertificateValidationCallback((sender, certificate, chain, errors) =>
                    ValidateRemoteCertificate(certificate, errors, expectedThumbprint)));

            await sslStream.AuthenticateAsClientAsync(ipAddress).ConfigureAwait(false);

            var session = new TcpSession(client, sslStream);
            RegisterSession(session);
            return session;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Network] Connection/SSL failed to {ipAddress}:{port}. Error: {ex.Message}");
            client.Dispose();
            throw;
        }
    }

    private static bool ValidateRemoteCertificate(X509Certificate? certificate, SslPolicyErrors errors, string? expectedThumbprint)
    {
        X509Certificate2? cert2 = certificate as X509Certificate2;
        if (cert2 is null && certificate is not null)
        {
            cert2 = new X509Certificate2(certificate);
        }

        if (cert2 is null)
        {
            Debug.WriteLine("[Network] Remote certificate validation failed: certificate was null.");
            return false;
        }

        string remoteThumbprint = NormalizeThumbprint(cert2.Thumbprint);
        string normalizedExpectedThumbprint = NormalizeThumbprint(expectedThumbprint);

        if (!string.IsNullOrWhiteSpace(normalizedExpectedThumbprint))
        {
            bool isMatch = string.Equals(remoteThumbprint, normalizedExpectedThumbprint, StringComparison.OrdinalIgnoreCase);
            if (!isMatch)
            {
                Debug.WriteLine($"[Network] Remote certificate thumbprint mismatch. Expected={normalizedExpectedThumbprint}, Actual={remoteThumbprint}");
            }

            return isMatch;
        }

        bool onlyExpectedSelfSignedErrors = errors is SslPolicyErrors.None
            or SslPolicyErrors.RemoteCertificateChainErrors
            or SslPolicyErrors.RemoteCertificateNameMismatch
            or (SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch);

        if (!onlyExpectedSelfSignedErrors)
        {
            Debug.WriteLine($"[Network] Remote certificate validation failed with unexpected policy errors: {errors}");
        }

        return onlyExpectedSelfSignedErrors;
    }

    private static string NormalizeThumbprint(string? thumbprint)
    {
        return string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
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
