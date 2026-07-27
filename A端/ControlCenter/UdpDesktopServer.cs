using RemoteControl.Shared;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ControlCenter;

public partial class MainWindow
{
  private UdpClient? _udpDesktopSocket;
  private CancellationTokenSource? _udpDesktopCts;
  private readonly ConcurrentDictionary<string, UdpPeerBinding> _udpPeerBindings =
    new(StringComparer.OrdinalIgnoreCase);

  private void StartUdpDesktopServer(int port, CancellationToken serverToken)
  {
    StopUdpDesktopServer();
    var socket = new UdpClient(AddressFamily.InterNetwork);
    socket.Client.ReceiveBufferSize = 16 * 1024 * 1024;
    socket.Client.SendBufferSize = 4 * 1024 * 1024;
    socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    _udpDesktopSocket = socket;
    _udpDesktopCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
    _ = Task.Run(
      () => UdpDesktopReceiveLoopAsync(socket, _udpDesktopCts.Token),
      CancellationToken.None);
  }

  private void StopUdpDesktopServer()
  {
    try { _udpDesktopCts?.Cancel(); } catch { }
    try { _udpDesktopSocket?.Dispose(); } catch { }
    _udpDesktopSocket = null;
    _udpDesktopCts?.Dispose();
    _udpDesktopCts = null;
    _udpPeerBindings.Clear();
  }

  private async Task UdpDesktopReceiveLoopAsync(
    UdpClient socket,
    CancellationToken token)
  {
    using MultimediaThreadScope multimediaPriority = MultimediaThreadScope.Enter("Playback");
    while (!token.IsCancellationRequested)
    {
      try
      {
        UdpReceiveResult received = await socket.ReceiveAsync(token);
        if (!UdpDesktopProtocol.TryParse(
              received.Buffer,
              out UdpDesktopDatagram? packet) ||
            packet is null)
          continue;

        string endpointKey = EndpointKey(received.RemoteEndPoint);
        if (packet.Kind == UdpDesktopPacketKind.Hello)
        {
          UdpPeerHello hello = UdpDesktopProtocol.ReadHello(packet);
          if (!_devicesById.TryGetValue(hello.DeviceId, out DeviceView? device) ||
              !device.IsOnline ||
              !device.AcceptsInstance(hello.InstanceId))
            continue;
          _udpPeerBindings[endpointKey] = new(
            hello.DeviceId,
            hello.InstanceId,
            packet.Role);
          await device.RegisterUdpPeerAsync(
            packet.Role,
            received.RemoteEndPoint,
            hello.InstanceId,
            packet.Sequence,
            token);
          continue;
        }

        if (!_udpPeerBindings.TryGetValue(endpointKey, out UdpPeerBinding binding) ||
            !_devicesById.TryGetValue(binding.DeviceId, out DeviceView? boundDevice) ||
            !boundDevice.IsOnline ||
            !boundDevice.AcceptsInstance(binding.InstanceId) ||
            binding.Role != packet.Role)
          continue;
        await boundDevice.HandleUdpDesktopDatagramAsync(
          packet,
          received.RemoteEndPoint,
          token);
      }
      catch (OperationCanceledException) { break; }
      catch (ObjectDisposedException) { break; }
      catch (SocketException) when (token.IsCancellationRequested) { break; }
      catch
      {
        if (!token.IsCancellationRequested)
          await Task.Delay(10, token).ContinueWith(_ => { });
      }
    }
  }

  private async Task SendUdpDesktopAsync(
    byte[] datagram,
    IPEndPoint endpoint,
    CancellationToken token)
  {
    UdpClient socket = _udpDesktopSocket
      ?? throw new InvalidOperationException("UDP桌面监听尚未启动。");
    await socket.SendAsync(datagram.AsMemory(), endpoint, token);
  }

  private static string EndpointKey(IPEndPoint endpoint) =>
    $"{endpoint.Address.MapToIPv6()}|{endpoint.Port}";

  private readonly record struct UdpPeerBinding(
    string DeviceId,
    Guid InstanceId,
    UdpDesktopPeerRole Role);
}
