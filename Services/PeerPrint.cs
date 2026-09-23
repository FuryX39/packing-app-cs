using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarehousePacking.Services;

/// <summary>
/// Печать на принтер другого компьютера, где запущено это же приложение.
/// Имя в настройках: [ИМЯ-ПК] Название принтера.
/// </summary>
public static class PeerPrint
{
    public const int DiscoveryPort = 47640;
    public const int JobPort = 47641;
    private static readonly byte[] Magic = [(byte)'W', (byte)'P', (byte)'P', (byte)'1'];
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;
    private static Socket? _udp;
    private static TcpListener? _listener;
    private static readonly Dictionary<string, Peer> Peers = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsRemote(string printer)
    {
        return TryParse(printer, out _, out _);
    }

    public static bool TryParse(string printer, out string computer, out string localPrinter)
    {
        computer = "";
        localPrinter = "";
        var text = (printer ?? "").Trim();
        if (!text.StartsWith('['))
            return false;
        var end = text.IndexOf("] ", StringComparison.Ordinal);
        if (end <= 1)
            return false;
        computer = text[1..end].Trim();
        localPrinter = text[(end + 2)..].Trim();
        return computer.Length > 0 && localPrinter.Length > 0;
    }

    public static string Format(string computer, string printer) => $"[{computer}] {printer}";

    public static void Start()
    {
        lock (Gate)
        {
            if (_cts is not null)
                return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _udp.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _listener = new TcpListener(IPAddress.Any, JobPort);
                _listener.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("PeerPrint start: " + ex.Message);
                Stop();
                return;
            }
            _ = Task.Run(() => ReceiveLoop(token));
            _ = Task.Run(() => BeaconLoop(token));
            _ = Task.Run(() => AcceptLoop(token));
        }
        Announce();
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { _cts?.Cancel(); } catch { /* already disposed */ }
            try { _udp?.Close(); } catch { /* socket already closed */ }
            try { _listener?.Stop(); } catch { /* listener already stopped */ }
            _udp = null;
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public static void Announce()
    {
        Socket? udp;
        lock (Gate)
            udp = _udp;
        if (udp is null)
            return;
        var msg = new PeerMessage
        {
            V = 1,
            Name = Environment.MachineName,
            Port = JobPort,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOpts);
        foreach (var ep in BroadcastEndpoints())
        {
            try { udp.SendTo(bytes, ep); }
            catch { /* one interface can fail, the rest still announce */ }
        }
    }

    public static IReadOnlyList<string> RemoteChoices()
    {
        List<Peer> peers;
        lock (Peers)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-12);
            peers = Peers.Values.Where(p => p.SeenUtc >= cutoff).ToList();
        }
        var result = new List<string>();
        foreach (var peer in peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.Equals(peer.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                continue;
            string[] printers;
            try
            {
                printers = QueryPrinters(peer);
            }
            catch
            {
                continue;
            }
            foreach (var name in printers)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                result.Add(Format(peer.Name, name.Trim()));
            }
        }
        return result;
    }

    public static void Print(IReadOnlyList<byte[]> pdfs, PrintProfile profile, int copies)
    {
        if (!TryParse(profile.Printer, out var computer, out var printer))
            throw new InvalidOperationException("Некорректное имя удалённого принтера");
        var pages = pdfs.Where(p => p is { Length: > 0 }).ToList();
        if (pages.Count == 0)
            throw new InvalidOperationException("Пустой PDF");
        var peer = FindPeer(computer);
        var request = new PeerMessage
        {
            Op = "print",
            Printer = printer,
            Settings = profile.Settings ?? "",
            Copies = Math.Clamp(copies, 1, 9999),
            Sizes = pages.Select(p => p.Length).ToList(),
        };
        using var tcp = new TcpClient();
        tcp.ReceiveTimeout = 180000;
        tcp.SendTimeout = 180000;
        try
        {
            var connect = tcp.ConnectAsync(peer.Address, peer.Port);
            if (!connect.Wait(TimeSpan.FromSeconds(4)))
                throw new IOException($"Компьютер {computer} не отвечает. Приложение упаковщиков на нём должно быть запущено.");
        }
        catch (AggregateException ex)
        {
            throw new IOException($"Компьютер {computer} не отвечает. Приложение упаковщиков на нём должно быть запущено.", ex.InnerException);
        }
        using var stream = tcp.GetStream();
        WriteFrame(stream, request);
        foreach (var pdf in pages)
            stream.Write(pdf, 0, pdf.Length);
        stream.Flush();
        var reply = ReadFrame(stream);
        if (!reply.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reply.Error) ? "Удалённая печать не выполнена" : reply.Error);
    }

    private static Peer FindPeer(string computer)
    {
        lock (Peers)
        {
            if (Peers.TryGetValue(computer, out var peer) && peer.SeenUtc >= DateTime.UtcNow.AddSeconds(-20))
                return peer;
        }
        Announce();
        Thread.Sleep(700);
        lock (Peers)
        {
            if (Peers.TryGetValue(computer, out var peer))
                return peer;
        }
        throw new IOException($"Компьютер {computer} не виден в сети. На нём должно быть запущено приложение упаковщиков, оба компьютера в одной частной сети.");
    }

    private static string[] QueryPrinters(Peer peer)
    {
        using var tcp = new TcpClient();
        tcp.ReceiveTimeout = 2500;
        tcp.SendTimeout = 2500;
        if (!tcp.ConnectAsync(peer.Address, peer.Port).Wait(TimeSpan.FromMilliseconds(1500)))
            throw new IOException("timeout");
        using var stream = tcp.GetStream();
        WriteFrame(stream, new PeerMessage { Op = "list" });
        var reply = ReadFrame(stream);
        return reply.Printers.ToArray();
    }

    private static void BeaconLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Announce();
                if (token.WaitHandle.WaitOne(2000))
                    return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static void ReceiveLoop(CancellationToken token)
    {
        var buf = new byte[8192];
        while (!token.IsCancellationRequested)
        {
            Socket? udp;
            lock (Gate)
                udp = _udp;
            if (udp is null)
                return;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                var n = udp.ReceiveFrom(buf, ref remote);
                if (n <= 0 || remote is not IPEndPoint ep || !IsPrivate(ep.Address))
                    continue;
                var msg = JsonSerializer.Deserialize<PeerMessage>(buf.AsSpan(0, n), JsonOpts);
                if (msg is null || msg.V != 1 || string.IsNullOrWhiteSpace(msg.Name))
                    continue;
                if (string.Equals(msg.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var port = msg.Port is > 0 and < 65536 ? msg.Port : JobPort;
                lock (Peers)
                {
                    Peers[msg.Name] = new Peer
                    {
                        Name = msg.Name.Trim(),
                        Address = ep.Address,
                        Port = port,
                        SeenUtc = DateTime.UtcNow,
                    };
                }
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                /* one bad datagram does not stop discovery */
            }
        }
    }

    private static void AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpListener? listener;
            lock (Gate)
                listener = _listener;
            if (listener is null)
                return;
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private static void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint ep || !IsPrivate(ep.Address))
                    return;
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 180000;
                var stream = client.GetStream();
                var request = ReadFrame(stream);
                if (string.Equals(request.Op, "list", StringComparison.OrdinalIgnoreCase))
                {
                    WriteFrame(stream, new PeerMessage
                    {
                        Ok = true,
                        Printers = GdiPrinter.InstalledPrinters().ToList(),
                    });
                    return;
                }
                if (!string.Equals(request.Op, "print", StringComparison.OrdinalIgnoreCase))
                {
                    WriteFrame(stream, new PeerMessage { Ok = false, Error = "Неизвестная команда" });
                    return;
                }
                if (!AcceptsRemote())
                {
                    WriteFrame(stream, new PeerMessage { Ok = false, Error = "На этом компьютере печать с других ПК выключена" });
                    return;
                }
                if (request.Sizes.Count == 0 || request.Sizes.Count > 40)
                {
                    WriteFrame(stream, new PeerMessage { Ok = false, Error = "Пустое задание печати" });
                    return;
                }
                long total = 0;
                foreach (var size in request.Sizes)
                {
                    if (size <= 0 || size > 20_000_000)
                    {
                        WriteFrame(stream, new PeerMessage { Ok = false, Error = "Слишком большой файл печати" });
                        return;
                    }
                    total += size;
                }
                if (total > 40_000_000)
                {
                    WriteFrame(stream, new PeerMessage { Ok = false, Error = "Слишком большой файл печати" });
                    return;
                }
                client.ReceiveTimeout = 180000;
                var pdfs = new List<byte[]>(request.Sizes.Count);
                foreach (var size in request.Sizes)
                {
                    var buf = new byte[size];
                    ReadExact(stream, buf, size);
                    pdfs.Add(buf);
                }
                GdiPrinter.PrintPdfs(pdfs, new PrintProfile(request.Printer, request.Settings ?? ""), Math.Clamp(request.Copies, 1, 9999));
                WriteFrame(stream, new PeerMessage { Ok = true });
            }
            catch (Exception ex)
            {
                try
                {
                    using var stream = client.GetStream();
                    WriteFrame(stream, new PeerMessage { Ok = false, Error = ex.Message });
                }
                catch
                {
                    /* client already gone */
                }
            }
        }
    }

    private static bool AcceptsRemote()
    {
        try
        {
            return AppConfig.Load().PeerPrintAccept;
        }
        catch
        {
            return true;
        }
    }

    private static void WriteFrame(NetworkStream stream, PeerMessage message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);
        var len = BitConverter.GetBytes(body.Length);
        stream.Write(Magic, 0, Magic.Length);
        stream.Write(len, 0, len.Length);
        stream.Write(body, 0, body.Length);
    }

    private static PeerMessage ReadFrame(NetworkStream stream)
    {
        var magic = new byte[4];
        ReadExact(stream, magic, 4);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new IOException("Компьютер ответил не тем протоколом");
        var lenBuf = new byte[4];
        ReadExact(stream, lenBuf, 4);
        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len < 2 || len > 1_000_000)
            throw new IOException("Некорректный ответ компьютера");
        var body = new byte[len];
        ReadExact(stream, body, len);
        return JsonSerializer.Deserialize<PeerMessage>(body, JsonOpts) ?? new PeerMessage();
    }

    private static void ReadExact(NetworkStream stream, byte[] buf, int len)
    {
        var off = 0;
        while (off < len)
        {
            var n = stream.Read(buf, off, len - off);
            if (n <= 0)
                throw new IOException("Связь с другим компьютером прервана");
            off += n;
        }
    }

    private static List<IPEndPoint> BroadcastEndpoints()
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, DiscoveryPort) };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null)
                    continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (ip.Length != 4 || mask.Length != 4)
                    continue;
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    bcast[i] = (byte)(ip[i] | (mask[i] ^ 0xFF));
                targets.Add(new IPEndPoint(new IPAddress(bcast), DiscoveryPort));
            }
        }
        return targets;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        var b = address.GetAddressBytes();
        if (b.Length != 4)
            return false;
        if (b[0] == 10)
            return true;
        if (b[0] == 192 && b[1] == 168)
            return true;
        if (b[0] == 172 && b[1] is >= 16 and <= 31)
            return true;
        if (b[0] == 169 && b[1] == 254)
            return true;
        return false;
    }

    private sealed class Peer
    {
        public string Name { get; set; } = "";
        public IPAddress Address { get; set; } = IPAddress.None;
        public int Port { get; set; }
        public DateTime SeenUtc { get; set; }
    }

    private sealed class PeerMessage
    {
        public int V { get; set; }
        public string Name { get; set; } = "";
        public int Port { get; set; }
        public string Op { get; set; } = "";
        public string Printer { get; set; } = "";
        public string Settings { get; set; } = "";
        public int Copies { get; set; } = 1;
        public List<int> Sizes { get; set; } = [];
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public List<string> Printers { get; set; } = [];
    }
}
