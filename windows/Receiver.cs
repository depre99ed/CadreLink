using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CadreLink;

internal static class Native {
    const string Library = "CadreLinkCamera.dll";
    [DllImport(Library)] public static extern int DecoderOpen(int w, int h, int fps, out IntPtr decoder);
    [DllImport(Library)] public static extern void DecoderClose(IntPtr decoder);
    [DllImport(Library)] public static extern int DecodeFrame(IntPtr decoder, byte[] bytes, int length, int fps, int rotation, out int frames);
    [DllImport(Library)] public static extern void SetWebcamHidden(bool hidden);
    [DllImport(Library)] public static extern void DecoderHide(IntPtr decoder, bool hidden);
    [DllImport(Library)] public static extern void DecoderFraming(IntPtr decoder, float x, float y, float zoom);
    [DllImport(Library)] public static extern bool TrackingPreview(IntPtr decoder, [Out] byte[] pixels, int w, int h);
    [DllImport(Library)] public static extern bool DecoderOutputSize(IntPtr decoder, out int width, out int height);
    [DllImport(Library)] public static extern bool PosePreview(IntPtr decoder, [Out] byte[] rgb, int size, int height);
    [DllImport(Library)] public static extern bool Preview(IntPtr decoder, [Out] byte[] pixels, int w, int h);
    [DllImport(Library)] public static extern int CameraSetup(bool remove);
}

internal sealed class PreviewFrame : IDisposable {
    public const int Width = 640, Height = 360, Stride = Width * 4, Length = Stride * Height;
    byte[]? data = ArrayPool<byte>.Shared.Rent(Length);
    public byte[] Pixels => data ?? throw new ObjectDisposedException(nameof(PreviewFrame));

    public void Dispose() {
        if (Interlocked.Exchange(ref data, null) is { } released)
            ArrayPool<byte>.Shared.Return(released, clearArray: true);
    }
}

// Keep the version 1 storage location to preserve the user's local settings and phone association.
internal static class AppData {
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Focale");
}

internal sealed class Receiver : IDisposable {
    public const int Port = 47472;
    const int MaxPacket = 4 * 1024 * 1024;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim activePeer = new(1, 1), writes = new(1, 1), connections = new(4, 4);
    readonly TcpListener listener;
    readonly X509Certificate2 certificate;
    readonly byte[] key;
    readonly string pairingQuery;
    readonly IPAddress preferredAddress;
    volatile SslStream? peer;
    TaskCompletionSource<StillFrame>? stillRequest;
    internal sealed record StillFrame(byte[] Pixels, int Width, int Height);
    volatile bool hidden;
    public volatile int Rotation;
    public volatile FramingMode Framing;
    public string TrackingState { get; private set; } = "Désactivé";
    public string Connection { get; private set; } = "";
    public bool Hidden {
        get => hidden;
        set { hidden = value; Native.SetWebcamHidden(value); }
    }

    public bool HasPeer => peer is not null;
    public event Action<string>? Tracking;
    public event Action<double, double>? Signal;
    public event Action<long>? Latency;
    public event Action<string>? Status;
    public event Action<JsonElement>? Metadata;
    public event Action<PreviewFrame>? Picture;
    public event Action<string>? Disconnected;

    public string Link => LinkFor(preferredAddress, false);
    // Preserve the Android URI scheme for phones already on version 1.
    public string LinkFor(IPAddress ip, bool usb) =>
        $"focale://pair?host={ip}&{pairingQuery}" + (usb ? "&transport=usb" : "");

    public Receiver(IPAddress ip, string? identityFolder = null, bool allInterfaces = false) {
        preferredAddress = ip;
        string directory = identityFolder ?? AppData.Folder;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "pairing.dat");
        if (!File.Exists(path)) {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=CadreLink", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(8));
            byte[] identity = [.. RandomNumberGenerator.GetBytes(32), .. cert.Export(X509ContentType.Pfx)];
            try {
                string temporary = path + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, ProtectedData.Protect(identity, null, DataProtectionScope.CurrentUser));
                File.Move(temporary, path);
            } finally {
                CryptographicOperations.ZeroMemory(identity);
            }
        }
        byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try {
            key = data[..32];
            certificate = new X509Certificate2(data.AsSpan(32), (string?)null, X509KeyStorageFlags.UserKeySet);
        } finally {
            CryptographicOperations.ZeroMemory(data);
        }
        pairingQuery = $"pin={Convert.ToHexString(SHA256.HashData(certificate.RawData))}&key={Convert.ToHexString(key)}";
        listener = new TcpListener(allInterfaces ? IPAddress.Any : ip, Port);
        listener.Start(4);
    }

    internal sealed record Adapter(IPAddress Address, string Name, bool IsUsb) {
        public string Display => $"{Name} · {Address}";
    }

    public static List<Adapter> Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderByDescending(adapter => adapter.GetIPProperties().GatewayAddresses.Count > 0)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !address.Address.ToString().StartsWith("169.254."))
            .Select(address => new Adapter(address.Address, adapter.Name, IsUsbAdapter(adapter.Description))))
        .DistinctBy(adapter => adapter.Address).ToList();

    static bool IsUsbAdapter(string description) =>
        new[] { "rndis", "ndis compatible", "usb ethernet", "usb ncm", "remote ndis" }
            .Any(label => description.Contains(label, StringComparison.OrdinalIgnoreCase));

    public static List<IPAddress> Addresses() => Adapters().Select(adapter => adapter.Address).ToList();

    public async Task Listen() {
        try {
            while (!stop.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (connections.Wait(0)) _ = ServeBounded(client);
                else client.Dispose();
            }
        } catch (Exception error) when (stop.IsCancellationRequested &&
            error is OperationCanceledException or ObjectDisposedException or SocketException) {}
    }

    async Task ServeBounded(TcpClient client) {
        try { await Serve(client); }
        finally { connections.Release(); }
    }

    readonly struct Packet(byte type, byte[] bytes, int size) : IDisposable {
        public byte Type { get; } = type;
        public byte[] Bytes { get; } = bytes;
        public int Length { get; } = size;
        public JsonElement Json() => JsonSerializer.Deserialize<JsonElement>(Bytes.AsSpan(0, Length));
        public void Dispose() => ArrayPool<byte>.Shared.Return(Bytes, clearArray: true);
    }

    static async Task<Packet> Read(SslStream stream, byte[] header, CancellationToken token, int limit) {
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length <= 0 || length > limit) throw new InvalidDataException("Paquet vidéo invalide");
        byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
        try {
            await stream.ReadExactlyAsync(bytes.AsMemory(0, length), token);
            return new Packet(header[0], bytes, length);
        } catch {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            throw;
        }
    }

    public Task Send(object value) => peer is { } target ? Line(target, JsonSerializer.Serialize(value)) : Task.CompletedTask;
    public void Reconnect() => peer?.Dispose();

    public async Task<StillFrame> Capture() {
        if (!HasPeer) throw new InvalidOperationException("La caméra est hors ligne");
        var request = new TaskCompletionSource<StillFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref stillRequest, request, null) is not null)
            throw new InvalidOperationException("Une photo est déjà en cours");
        try { return await request.Task.WaitAsync(TimeSpan.FromSeconds(2), stop.Token); }
        finally { Interlocked.CompareExchange(ref stillRequest, null, request); }
    }

    void ReadStill(IntPtr decoder) {
        if (Interlocked.Exchange(ref stillRequest, null) is not { } request) return;
        if (!Native.DecoderOutputSize(decoder, out int width, out int height)) {
            request.TrySetException(new InvalidOperationException("Image indisponible"));
            return;
        }
        var pixels = new byte[checked(width * height * 4)];
        if (Native.Preview(decoder, pixels, width, height)) request.TrySetResult(new StillFrame(pixels, width, height));
        else request.TrySetException(new InvalidOperationException("Image indisponible"));
    }

    async Task Line(SslStream target, string line) {
        await writes.WaitAsync(stop.Token);
        try { await target.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), stop.Token); }
        finally { writes.Release(); }
    }

    async Task Serve(TcpClient client) {
        var follow = new AutoFraming();
        int lastRotation = -1, fps = 30, acknowledged = 0, sampleFrames = 0;
        long lastPreview = 0, sampleBytes = 0, sampleStarted = Environment.TickCount64, lastDecoded = sampleStarted;
        long nextPing = 0, pendingPing = 0;
        var header = new byte[5];
        IntPtr decoder = IntPtr.Zero;
        bool locked = false;
        string reason = "Téléphone déconnecté · reconnexion en attente";

        using (client)
        using (var ssl = new SslStream(client.GetStream()))
        try {
            client.NoDelay = true;
            using var auth = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            auth.CancelAfter(6000);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, auth.Token);
            using (var hello = await Read(ssl, header, auth.Token, 2048)) {
                if (hello.Type != 1) return;
                var info = hello.Json();
                if (info.GetProperty("type").GetString() != "hello" ||
                    !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(info.GetProperty("key").GetString()!), key))
                    return;
                if (!(locked = activePeer.Wait(0))) return;
                peer = ssl;
                Connection = info.TryGetProperty("transport", out var transport) && transport.GetString() == "usb" ? "USB" : "Réseau";
                await Line(ssl, "ready");
                Status?.Invoke("Connecté · " + info.GetProperty("model").GetString());
            }
            lastDecoded = Environment.TickCount64;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            while (!stop.IsCancellationRequested) {
                timeout.CancelAfter(12000);
                using var packet = await Read(ssl, header, timeout.Token, MaxPacket);
                long now = Environment.TickCount64;
                if (packet.Type == 1) {
                    var obj = packet.Json();
                    string? type = obj.GetProperty("type").GetString();
                    if (type == "format") {
                        int width = obj.GetProperty("width").GetInt32(), height = obj.GetProperty("height").GetInt32();
                        fps = obj.GetProperty("fps").GetInt32();
                        if (width is < 16 or > 3840 || height is < 16 or > 2160 ||
                            (width & 1) != 0 || (height & 1) != 0 || fps is < 1 or > 120)
                            throw new InvalidDataException("Format vidéo incompatible");
                        follow.Reset();
                        Native.DecoderClose(decoder);
                        decoder = IntPtr.Zero;
                        Marshal.ThrowExceptionForHR(Native.DecoderOpen(width, height, fps, out decoder));
                        lastDecoded = now;
                    }
                    if (type == "pong") {
                        long timestamp = obj.GetProperty("time").GetInt64();
                        if (timestamp == pendingPing && now - timestamp is >= 0 and < 15000)
                            Latency?.Invoke(now - timestamp);
                    } else {
                        Metadata?.Invoke(obj);
                    }
                } else if (packet.Type == 2 && decoder != IntPtr.Zero) {
                    int rotation = Rotation;
                    if (lastRotation != rotation) { follow.Reset(); lastRotation = rotation; }
                    var crop = follow.BeforeFrame(Framing, now);
                    Native.DecoderFraming(decoder, crop.X, crop.Y, crop.Zoom);
                    Native.DecoderHide(decoder, Hidden);
                    Marshal.ThrowExceptionForHR(Native.DecodeFrame(decoder, packet.Bytes, packet.Length, fps, rotation, out int frames));
                    if (now >= nextPing) {
                        pendingPing = now;
                        nextPing = now + 1500;
                        await Line(ssl, $"{{\"ack\":{++acknowledged},\"ping\":{now}}}");
                    } else {
                        await Line(ssl, $"{{\"ack\":{++acknowledged}}}");
                    }
                    if (frames > 0) {
                        lastDecoded = now;
                        follow.Observe(decoder, now);
                        if (Volatile.Read(ref stillRequest) is not null) ReadStill(decoder);
                    }
                    if (TrackingState != follow.State) {
                        TrackingState = follow.State;
                        Tracking?.Invoke(TrackingState);
                    }
                    sampleFrames += frames;
                    sampleBytes += packet.Length;
                    long elapsed = now - sampleStarted;
                    if (elapsed >= 1000) {
                        Signal?.Invoke(1000.0 * sampleFrames / elapsed, sampleBytes * .008 / elapsed);
                        sampleStarted = now;
                        sampleFrames = 0;
                        sampleBytes = 0;
                    }
                    if (frames > 0 && now - lastPreview >= 45 && Picture is { } draw) {
                        lastPreview = now;
                        var preview = new PreviewFrame();
                        if (Native.Preview(decoder, preview.Pixels, PreviewFrame.Width, PreviewFrame.Height)) {
                            try { draw(preview); }
                            catch { preview.Dispose(); throw; }
                        } else {
                            preview.Dispose();
                        }
                    }
                }
                if (now - lastDecoded > 10000) throw new TimeoutException("La caméra ne fournit plus d’image");
            }
        } catch (Exception error) {
            if (locked && !stop.IsCancellationRequested)
                reason = error switch {
                    EndOfStreamException or ObjectDisposedException => reason,
                    OperationCanceledException => "Téléphone sans réponse · nouvelle tentative",
                    _ => $"Liaison interrompue · {error.Message}"
                };
        } finally {
            Native.DecoderClose(decoder);
            if (locked) {
                peer = null;
                Interlocked.Exchange(ref stillRequest, null)?.TrySetException(new InvalidOperationException("Téléphone déconnecté"));
                Disconnected?.Invoke(reason);
                activePeer.Release();
            }
        }
    }

    public void Dispose() {
        stop.Cancel();
        listener.Stop();
        peer?.Dispose();
        certificate.Dispose();
    }
}