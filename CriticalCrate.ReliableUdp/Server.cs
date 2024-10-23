using System.Net;
using CriticalCrate.ReliableUdp.Channels;

namespace CriticalCrate.ReliableUdp;

public sealed class Server(
    ISocket socket,
    IUnreliableChannel unreliableChannel,
    IReliableChannel reliableChannel,
    IPingChannel pingChannel,
    IPacketFactory packetFactory,
    IServerConnectionManager serverConnectionManager)
    : CriticalSocket(socket, unreliableChannel, reliableChannel, pingChannel, packetFactory, serverConnectionManager)
{
    public event Action<EndPoint>? OnConnected;
    public event Action<EndPoint>? OnDisconnected;
    public IServerConnectionManager ConnectionManager { get; } = serverConnectionManager;
    public IPEndPoint ServerEndpoint { get; private set; }

    public void Listen(IPEndPoint endPoint)
    {
        ServerEndpoint = endPoint;
        socket.Listen(endPoint);
        ConnectionManager.OnConnected += HandleConnected;
        ConnectionManager.OnDisconnected += HandleDisconnected;
    }


    private void HandleDisconnected(EndPoint endPoint)
    {
        reliableChannel.HandleDisconnection(endPoint);
        pingChannel.HandleDisconnection(endPoint);
        OnDisconnected?.Invoke(endPoint);
    }

    private void HandleConnected(EndPoint endPoint)
    {
        reliableChannel.HandleConnection(endPoint);
        pingChannel.HandleConnection(endPoint);
        OnConnected?.Invoke(endPoint);
    }
}
