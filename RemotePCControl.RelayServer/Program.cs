using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RemotePCControl.RelayServer;

class Program
{
    // Code -> HostSession
    private static readonly ConcurrentDictionary<string, ClientSession> _pendingHosts = new();

    static async Task Main(string[] args)
    {
        int port = 9000;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Relay] Server started on port {port} (TCP)");
        Console.WriteLine("[Relay] Protocol: 0x30 (Register Host), 0x31 (Connect Client)");

        while (true)
        {
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(tcpClient);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Relay] Accept error: {ex.Message}");
            }
        }
    }

    static async Task HandleClientAsync(TcpClient tcpClient)
    {
        var session = new ClientSession(tcpClient);
        try
        {
            await session.ProcessAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Session {session.Id} error: {ex.Message}");
        }
        finally
        {
            session.Dispose();
        }
    }

    public static bool TryRegisterHost(string code, ClientSession host)
    {
        if (_pendingHosts.TryAdd(code, host))
        {
            Console.WriteLine($"[Relay] Host registered with code: {code} (ID: {host.Id})");
            return true;
        }
        return false;
    }

    public static ClientSession? TryPairClient(string code, ClientSession client)
    {
        if (_pendingHosts.TryRemove(code, out var host))
        {
            Console.WriteLine($"[Relay] Pairing successful for code: {code} (Host: {host.Id}, Client: {client.Id})");
            return host;
        }
        return null;
    }

    public static void UnregisterHost(string? code)
    {
        if (code != null) _pendingHosts.TryRemove(code, out _);
    }
}

public sealed class ClientSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _isDisposed;
    private string? _code;
    private ClientSession? _peer;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public ClientSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _client.NoDelay = true;
    }

    public async Task ProcessAsync()
    {
        var header = new byte[4];

        while (true)
        {
            // Read Packet Length
            if (await ReadExactAsync(_stream, header) == 0) break;
            int length = BitConverter.ToInt32(header, 0);

            if (length <= 0 || length > 10 * 1024 * 1024) break;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (await ReadExactAsync(_stream, buffer, length) == 0) break;

                if (_peer != null)
                {
                    // Already paired, just pipe data
                    await _peer.SendAsync(buffer.AsMemory(0, length));
                }
                else
                {
                    // Control logic (0x30, 0x31)
                    byte type = buffer[0];
                    string code = Encoding.UTF8.GetString(buffer, 1, length - 1);
                    _code = code;

                    if (type == 0x30) // Register as Host
                    {
                        if (!Program.TryRegisterHost(code, this))
                        {
                            await SendAsync(new byte[] { 0x32, 0x00 }); // Error: Duplicate Code
                            break;
                        }
                        await SendAsync(new byte[] { 0x30, 0x01 }); // OK: Registered
                        
                        // Stay in loop waiting for peer
                        while (_peer == null && !_isDisposed)
                        {
                            await Task.Delay(500);
                        }
                    }
                    else if (type == 0x31) // Connect to Host
                    {
                        var host = Program.TryPairClient(code, this);
                        if (host == null)
                        {
                            await SendAsync(new byte[] { 0x32, 0x01 }); // Error: Code Not Found
                            break;
                        }

                        // Link both
                        _peer = host;
                        host._peer = this;

                        await SendAsync(new byte[] { 0x31, 0x01 }); // OK: Connected to Host
                        await host.SendAsync(new byte[] { 0x31, 0x02 }); // Notify Host: Client Connected
                    }
                    else
                    {
                        break; 
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        if (_isDisposed) return;
        await _writeLock.WaitAsync();
        try
        {
            byte[] header = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(header);
            await _stream.WriteAsync(data);
            await _stream.FlushAsync();
        }
        catch { Dispose(); }
        finally { _writeLock.Release(); }
    }

    private async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int? length = null)
    {
        int target = length ?? buffer.Length;
        int total = 0;
        while (total < target)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, target - total));
            if (read == 0) return 0;
            total += read;
        }
        return total;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Program.UnregisterHost(_code);
        _client.Dispose();
        _writeLock.Dispose();
        if (_peer != null)
        {
            _peer._peer = null;
            _peer.Dispose();
        }
    }
}
