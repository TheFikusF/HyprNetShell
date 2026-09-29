using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyprNetShell.Core.Features.KdeConnect.Protocol;

namespace HyprNetShell.Core.Features.KdeConnect.Transport;

internal sealed class KdeConnectChannel : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly KdeConnectPacketStream _stream;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    internal KdeConnectIdentity RemoteIdentity { get; }
    internal byte[] RemoteCertificateDer { get; }
    internal bool IsOutbound { get; }
    internal Task Completion { get; private set; } = Task.CompletedTask;

    private KdeConnectChannel(
        TcpClient client,
        KdeConnectPacketStream stream,
        KdeConnectIdentity remoteIdentity,
        byte[] remoteCertificateDer,
        bool isOutbound)
    {
        _client = client;
        _stream = stream;
        RemoteIdentity = remoteIdentity;
        RemoteCertificateDer = remoteCertificateDer;
        IsOutbound = isOutbound;
    }

    internal static async Task<KdeConnectChannel> ConnectAsync(
        string host,
        int port,
        KdeConnectIdentity expectedIdentity,
        KdeConnectStateStore state,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var timeout = CreateHandshakeCancellation(cancellationToken);
            await client.ConnectAsync(host, port, timeout.Token);
            var network = client.GetStream();
            await network.WriteAsync(
                KdeConnectProtocol.CreateConnectionIdentity(
                    state.DeviceId,
                    state.DeviceName,
                    expectedIdentity.DeviceId),
                timeout.Token);
            await network.FlushAsync(timeout.Token);

            X509Certificate2? remoteCertificate = null;
            var ssl = new SslStream(
                network,
                leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) => ValidateRemoteCertificate(
                    certificate,
                    expectedIdentity.DeviceId,
                    state.GetPaired(expectedIdentity.DeviceId),
                    out remoteCertificate));
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = state.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                timeout.Token);

            return await CompleteHandshakeAsync(
                client,
                ssl,
                expectedIdentity,
                remoteCertificate,
                state,
                isOutbound: true,
                timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task<KdeConnectChannel> AcceptAsync(
        TcpClient client,
        KdeConnectStateStore state,
        CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            using var timeout = CreateHandshakeCancellation(cancellationToken);
            var network = client.GetStream();
            var plaintext = await ReadPlaintextPacketAsync(network, timeout.Token);
            using var packet = plaintext;
            if (!KdeConnectProtocol.TryReadIdentity(packet, out var expectedIdentity) ||
                expectedIdentity.ProtocolVersion != KdeConnectProtocol.Version ||
                !string.Equals(expectedIdentity.TargetDeviceId, state.DeviceId, StringComparison.Ordinal) ||
                expectedIdentity.TargetProtocolVersion != KdeConnectProtocol.Version)
            {
                throw new AuthenticationException("Invalid plaintext KDE Connect identity.");
            }

            X509Certificate2? remoteCertificate = null;
            var certificates = new X509Certificate2Collection(state.Certificate);
            var ssl = new SslStream(
                network,
                leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) => ValidateRemoteCertificate(
                    certificate,
                    expectedIdentity.DeviceId,
                    state.GetPaired(expectedIdentity.DeviceId),
                    out remoteCertificate));
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = expectedIdentity.DeviceId,
                    ClientCertificates = certificates,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                timeout.Token);

            return await CompleteHandshakeAsync(
                client,
                ssl,
                expectedIdentity,
                remoteCertificate,
                state,
                isOutbound: false,
                timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<KdeConnectChannel> CompleteHandshakeAsync(
        TcpClient client,
        SslStream ssl,
        KdeConnectIdentity preTlsIdentity,
        X509Certificate2? remoteCertificate,
        KdeConnectStateStore state,
        bool isOutbound,
        CancellationToken cancellationToken)
    {
        if (remoteCertificate is null)
        {
            ssl.Dispose();
            throw new AuthenticationException("The KDE Connect peer did not present a certificate.");
        }

        var packetStream = new KdeConnectPacketStream(ssl);
        try
        {
            await packetStream.WriteAsync(
                KdeConnectProtocol.CreateIdentity(state.DeviceId, state.DeviceName, state.DeviceType),
                cancellationToken);
            using var identityPacket = await packetStream.ReadAsync(
                cancellationToken,
                KdeConnectProtocol.MaximumIdentityBytes);
            if (!KdeConnectProtocol.TryReadIdentity(identityPacket, out var encryptedIdentity) ||
                encryptedIdentity.ProtocolVersion != preTlsIdentity.ProtocolVersion ||
                !string.Equals(encryptedIdentity.DeviceId, preTlsIdentity.DeviceId, StringComparison.Ordinal) ||
                !string.Equals(
                    remoteCertificate.GetNameInfo(X509NameType.SimpleName, false),
                    encryptedIdentity.DeviceId,
                    StringComparison.Ordinal))
            {
                throw new AuthenticationException("Plaintext, encrypted, and certificate KDE Connect identities do not match.");
            }

            return new KdeConnectChannel(
                client,
                packetStream,
                encryptedIdentity with { TcpPort = preTlsIdentity.TcpPort },
                remoteCertificate.RawDataMemory.ToArray(),
                isOutbound);
        }
        catch
        {
            packetStream.Dispose();
            throw;
        }
        finally
        {
            remoteCertificate.Dispose();
        }
    }

    internal void Start(Func<KdeConnectChannel, KdeConnectPacket, CancellationToken, Task> packetHandler)
    {
        Completion = Task.Run(() => ReadLoopAsync(packetHandler, _lifetime.Token));
    }

    internal Task SendAsync(byte[] packet, CancellationToken cancellationToken) =>
        _stream.WriteAsync(packet, cancellationToken);

    private async Task ReadLoopAsync(
        Func<KdeConnectChannel, KdeConnectPacket, CancellationToken, Task> packetHandler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var packet = await _stream.ReadAsync(cancellationToken);
            await packetHandler(this, packet, cancellationToken);
        }
    }

    private static async Task<KdeConnectPacket> ReadPlaintextPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[1];
        while (bytes.Count < KdeConnectProtocol.MaximumIdentityBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("KDE Connect peer closed before sending its identity.");
            }
            if (buffer[0] == (byte)'\n')
            {
                if (KdeConnectProtocol.TryParse(bytes.ToArray(), out var packet))
                {
                    return packet!;
                }
                throw new InvalidDataException("Malformed plaintext KDE Connect identity.");
            }
            bytes.Add(buffer[0]);
        }
        throw new InvalidDataException("Plaintext KDE Connect identity exceeds the size limit.");
    }

    private static bool ValidateRemoteCertificate(
        X509Certificate? presented,
        string expectedDeviceId,
        PairedDevice? paired,
        out X509Certificate2? accepted)
    {
        accepted = null;
        if (presented is null)
        {
            return false;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
        }
        catch (CryptographicException)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime() ||
            !string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), expectedDeviceId, StringComparison.Ordinal))
        {
            certificate.Dispose();
            return false;
        }

        if (paired is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(certificate.RawDataMemory.Span, paired.CertificateDer))
            {
                certificate.Dispose();
                return false;
            }
            accepted = certificate;
            return true;
        }

        if (!string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal))
        {
            certificate.Dispose();
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (!chain.Build(certificate))
        {
            certificate.Dispose();
            return false;
        }

        accepted = certificate;
        return true;
    }

    private static CancellationTokenSource CreateHandshakeCancellation(CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(HandshakeTimeout);
        return cancellation;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        _stream.Dispose();
        _client.Dispose();
        _lifetime.Dispose();
    }
}
