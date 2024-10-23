using System.Net;

namespace CriticalCrate.ReliableUdp;

public interface ISocket : IDisposable
{
    const int Mtu = 508;
    event OnPacketReceived OnPacketReceived;
    void Listen(EndPoint endPoint);
    void Send(Packet packet);
    bool Pool();
}

public delegate void OnPacketReceived(Packet packet);