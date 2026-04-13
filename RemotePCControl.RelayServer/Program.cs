using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePCControl.RelayServer;

internal static partial class Program
{
    private const int DefaultPort = 9000;
    private const int MaxPayloadLength = 10 * 1024 * 1024;
    private static readonly TimeSpan PendingHostTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, PendingHostRegistration> PendingHosts = new();

    public static async Task Main(string[] args)
    {
        using var cleanupCts = new CancellationTokenSource();
        _ = RunPendingHostCleanupLoopAsync(cleanupCts.Token);

        var listener = new TcpListener(IPAddress.Any, DefaultPort);
        listener.Start();
        Log("ServerStarted", $"Relay server started on port {DefaultPort} (TCP).");
        Log("Protocol", "Protocol: 0x30 (Register Host), 0x31 (Connect Client)");

        while (true)
        {
            try
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = HandleClientAsync(tcpClient);
            }
            catch (Exception ex)
            {
                Log("AcceptError", $"Client accept failed. {ex.Message}");
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient tcpClient)
    {
        using var session = new ClientSession(tcpClient);
        try
        {
            await session.ProcessAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("SessionError", $"Session {session.Id} failed. {ex.Message}");
        }
    }

    public static bool TryRegisterHost(string code, ClientSession host, out RelayRegisterResult result)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var registration = new PendingHostRegistration(code, host, now, now.Add(PendingHostTtl));

        while (true)
        {
            if (PendingHosts.TryGetValue(code, out PendingHostRegistration? existing))
            {
                if (existing.IsExpired(now) || !existing.HostSession.IsAlive)
                {
                    if (PendingHosts.TryRemove(new KeyValuePair<string, PendingHostRegistration>(code, existing)))
                    {
                        Log("RegisterCleanup", $"Removed stale relay host registration. Code={code}, Session={existing.HostSession.Id}");
                        existing.HostSession.Dispose("Stale relay registration replaced");
                        continue;
                    }

                    continue;
                }

                result = RelayRegisterResult.DuplicateCode;
                Log("RegisterRejected", $"Duplicate relay host code rejected. Code={code}, ExistingSession={existing.HostSession.Id}, NewSession={host.Id}");
                return false;
            }

            if (PendingHosts.TryAdd(code, registration))
            {
                result = RelayRegisterResult.Registered;
                host.SetPendingCode(code);
                Log("Register", $"Host registered. Code={code}, Session={host.Id}, ExpiresAt={registration.ExpiresAt:O}");
                return true;
            }
        }
    }

    public static RelayPairingResult TryPairClient(string code, ClientSession client, out ClientSession? host)
    {
        host = null;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!PendingHosts.TryGetValue(code, out PendingHostRegistration? registration))
        {
            Log("PairFailed", $"Relay host not found for code {code}. Client={client.Id}");
            return RelayPairingResult.CodeNotFound;
        }

        if (registration.IsExpired(now))
        {
            if (PendingHosts.TryRemove(new KeyValuePair<string, PendingHostRegistration>(code, registration)))
            {
                Log("Expired", $"Relay host registration expired before pairing. Code={code}, Host={registration.HostSession.Id}");
                registration.HostSession.Dispose("Relay host registration expired");
            }

            return RelayPairingResult.HostExpired;
        }

        if (!registration.HostSession.IsAlive)
        {
            PendingHosts.TryRemove(new KeyValuePair<string, PendingHostRegistration>(code, registration));
            Log("PairFailed", $"Relay host was already disconnected. Code={code}, Host={registration.HostSession.Id}, Client={client.Id}");
            return RelayPairingResult.HostExpired;
        }

        if (!PendingHosts.TryRemove(new KeyValuePair<string, PendingHostRegistration>(code, registration)))
        {
            Log("PairRetry", $"Relay pairing raced with another operation. Code={code}, Client={client.Id}");
            return RelayPairingResult.CodeNotFound;
        }

        host = registration.HostSession;
        host.ClearPendingCode(code);
        Log("PairSuccess", $"Relay pairing established. Code={code}, Host={host.Id}, Client={client.Id}");
        return RelayPairingResult.Paired;
    }

    public static void UnregisterHost(string? code, ClientSession session, string reason)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        if (PendingHosts.TryGetValue(code, out PendingHostRegistration? registration) &&
            ReferenceEquals(registration.HostSession, session) &&
            PendingHosts.TryRemove(new KeyValuePair<string, PendingHostRegistration>(code, registration)))
        {
            Log("Unregister", $"Relay host removed from pending list. Code={code}, Session={session.Id}, Reason={reason}");
        }
    }

    private static async Task RunPendingHostCleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach ((string code, PendingHostRegistration registration) in PendingHosts)
                {
                    if (!registration.IsExpired(now) && registration.HostSession.IsAlive)
                    {
                        continue;
                    }

                    if (PendingHosts.TryRemove(new KeyValuePair<string, PendingHostRegistration>(code, registration)))
                    {
                        try
                        {
                            string reason = registration.IsExpired(now)
                                ? "Relay host registration expired"
                                : "Relay host session is no longer alive";
                            Log("Cleanup", $"Removed pending relay host. Code={code}, Session={registration.HostSession.Id}, Reason={reason}");
                            registration.HostSession.Dispose(reason);
                        }
                        catch (Exception ex)
                        {
                            Log("CleanupError", $"Error during host disposal in cleanup: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("CleanupStopped", "Relay cleanup loop stopped.");
        }
    }

    public static bool TryNormalizeCode(string? rawCode, out string normalizedCode)
    {
        normalizedCode = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return false;
        }

        string candidate = rawCode.Trim().ToUpperInvariant();
        if (candidate.Length != 6 || !RelayCodeRegex().IsMatch(candidate))
        {
            return false;
        }

        normalizedCode = candidate;
        return true;
    }

    public static void Log(string category, string message)
    {
        Console.WriteLine($"[Relay][{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}][{category}] {message}");
    }

    [GeneratedRegex("^[A-Z0-9]{6}$", RegexOptions.Compiled)]
    private static partial Regex RelayCodeRegex();
}

internal sealed record PendingHostRegistration(
    string Code,
    ClientSession HostSession,
    DateTimeOffset RegisteredAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

internal enum RelayRegisterResult : byte
{
    Registered = 0x01,
    DuplicateCode = 0x00,
    InvalidCode = 0x02
}

internal enum RelayPairingResult : byte
{
    Paired = 0x00,
    CodeNotFound = 0x01,
    InvalidCode = 0x02,
    HostExpired = 0x03
}

public sealed class ClientSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _syncRoot = new();
    private bool _isDisposed;
    private string? _pendingCode;
    private ClientSession? _peer;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsAlive => !_isDisposed && _client.Connected;

    public ClientSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _client.NoDelay = true;
    }

    public async Task ProcessAsync()
    {
        byte[] header = new byte[sizeof(int)];

        while (!_isDisposed)
        {
            if (await ReadExactAsync(_stream, header).ConfigureAwait(false) == 0)
            {
                break;
            }

            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 10 * 1024 * 1024)
            {
                Program.Log("InvalidPayload", $"Session {Id} sent invalid payload length {length}.");
                break;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (await ReadExactAsync(_stream, buffer, length).ConfigureAwait(false) == 0)
                {
                    break;
                }

                ClientSession? peer = GetPeer();
                if (peer is not null)
                {
                    await peer.SendAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
                    continue;
                }

                await HandleControlPacketAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        if (_isDisposed)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed)
            {
                return;
            }

            byte[] header = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(header).ConfigureAwait(false);
            await _stream.WriteAsync(data).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Program.Log("SendFailed", $"Session {Id} send failed. {ex.Message}");
            Dispose("Send failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void SetPendingCode(string code)
    {
        lock (_syncRoot)
        {
            _pendingCode = code;
        }
    }

    public void ClearPendingCode(string code)
    {
        lock (_syncRoot)
        {
            if (string.Equals(_pendingCode, code, StringComparison.Ordinal))
            {
                _pendingCode = null;
            }
        }
    }

    public void PairWith(ClientSession peer)
    {
        lock (_syncRoot)
        {
            _peer = peer;
            _pendingCode = null;
        }
    }

    public ClientSession? GetPeer()
    {
        lock (_syncRoot)
        {
            return _peer;
        }
    }

    private async Task HandleControlPacketAsync(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            Program.Log("ControlError", $"Session {Id} sent empty control payload.");
            Dispose("Empty control payload");
            return;
        }

        byte type = payload.Span[0];
        string rawCode = Encoding.UTF8.GetString(payload.Span[1..]);
        if (!Program.TryNormalizeCode(rawCode, out string code))
        {
            Program.Log("InvalidCode", $"Session {Id} sent invalid relay code '{rawCode}'.");
            await SendAsync(new byte[] { 0x32, 0x02 }).ConfigureAwait(false);
            Dispose("Invalid relay code");
            return;
        }

        switch (type)
        {
            case 0x30:
                await RegisterHostAsync(code).ConfigureAwait(false);
                break;

            case 0x31:
                await ConnectClientAsync(code).ConfigureAwait(false);
                break;

            default:
                Program.Log("ControlError", $"Session {Id} sent unsupported control packet type 0x{type:X2}.");
                Dispose("Unsupported control packet");
                break;
        }
    }

    private async Task RegisterHostAsync(string code)
    {
        if (!Program.TryRegisterHost(code, this, out RelayRegisterResult result))
        {
            await SendAsync(new byte[] { 0x32, (byte)result }).ConfigureAwait(false);
            Dispose("Relay host registration rejected");
            return;
        }

        await SendAsync(new byte[] { 0x30, (byte)RelayRegisterResult.Registered }).ConfigureAwait(false);
    }

    private async Task ConnectClientAsync(string code)
    {
        RelayPairingResult pairingResult = Program.TryPairClient(code, this, out ClientSession? host);
        if (pairingResult != RelayPairingResult.Paired || host is null)
        {
            await SendAsync(new byte[] { 0x32, (byte)pairingResult }).ConfigureAwait(false);
            Dispose("Relay client pairing failed");
            return;
        }

        PairWith(host);
        host.PairWith(this);

        await SendAsync(new byte[] { 0x31, 0x01 }).ConfigureAwait(false);
        await host.SendAsync(new byte[] { 0x31, 0x02 }).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int? length = null)
    {
        int target = length ?? buffer.Length;
        int total = 0;
        while (total < target)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, target - total)).ConfigureAwait(false);
            if (read == 0)
            {
                return 0;
            }

            total += read;
        }

        return total;
    }

    public void Dispose()
    {
        Dispose("Session disposed");
    }

    public void Dispose(string reason)
    {
        ClientSession? peerToDetach = null;
        string? pendingCode = null;

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pendingCode = _pendingCode;
            _pendingCode = null;
            peerToDetach = _peer;
            _peer = null;
        }

        Program.UnregisterHost(pendingCode, this, reason);
        Program.Log("Disconnected", $"Session {Id} closed. Reason={reason}");

        if (peerToDetach is not null)
        {
            peerToDetach.DetachPeer(this, $"Peer {Id} disconnected");
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            Program.Log("DisposeWarning", $"Stream dispose failed for session {Id}. {ex.Message}");
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception ex)
        {
            Program.Log("DisposeWarning", $"TcpClient dispose failed for session {Id}. {ex.Message}");
        }

        _writeLock.Dispose();
    }

    private void DetachPeer(ClientSession expectedPeer, string reason)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_peer, expectedPeer))
            {
                return;
            }

            _peer = null;
        }

        Program.Log("PeerDetached", $"Session {Id} peer detached. Reason={reason}");
        
        // 페어가 연결을 끊었으므로 현재 세션도 자동으로 종료하여 리소스 낭비를 방지하고 클라이언트에 알림
        Dispose($"Peer disconnected: {reason}");
    }
}
