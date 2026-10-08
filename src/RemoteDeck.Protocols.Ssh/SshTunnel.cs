using Renci.SshNet;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// A port on this computer that leads, through a jump host, to the real target. The target connection is made to
/// that local port, so SSH to the target still runs end to end: the jump host only carries encrypted bytes.
/// </summary>
internal sealed class SshTunnel : IDisposable
{
    private readonly SshClient _jumpClient;
    private readonly ForwardedPortLocal _port;
    private readonly SshTunnel? _outer;

    private SshTunnel(SshClient jumpClient, ForwardedPortLocal port, SshTunnel? outer)
    {
        _jumpClient = jumpClient;
        _port = port;
        _outer = outer;
    }

    /// <summary>
    /// Builds the connection info for <paramref name="request"/>. With a jump host, it first connects through the
    /// whole chain and points the info at the local end of the tunnel.
    /// </summary>
    public static async Task<(ConnectionInfo Info, SshTunnel? Tunnel)> PrepareAsync(
        SshConnectRequest request,
        List<byte[]> wipe,
        CancellationToken cancellationToken)
    {
        if (request.Jump is null)
        {
            return (SshNetSessionFactory.BuildInfo(request, wipe), null);
        }

        var (jumpInfo, outer) = await PrepareAsync(request.Jump, wipe, cancellationToken).ConfigureAwait(false);
        SshClient? jumpClient = null;
        try
        {
            jumpClient = new SshClient(jumpInfo);
            SshNetSessionFactory.Configure(jumpClient, request.Jump);

            var client = jumpClient;
            try
            {
                await Task.Run(() => client.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw SshNetSessionFactory.Translate(ex, request.Jump);
            }

            var port = new ForwardedPortLocal("127.0.0.1", request.Host, (uint)request.Port);
            jumpClient.AddForwardedPort(port);
            port.Start();

            var tunnel = new SshTunnel(jumpClient, port, outer);
            return (SshNetSessionFactory.BuildInfo(request, wipe, "127.0.0.1", (int)port.BoundPort), tunnel);
        }
        catch
        {
            jumpClient?.Dispose();
            outer?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _port.Stop();
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException)
        {
            // Already stopped.
        }

        try
        {
            if (_jumpClient.IsConnected)
            {
                _jumpClient.Disconnect();
            }
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or IOException)
        {
            // Already gone.
        }

        _port.Dispose();
        _jumpClient.Dispose();
        _outer?.Dispose();
    }
}
