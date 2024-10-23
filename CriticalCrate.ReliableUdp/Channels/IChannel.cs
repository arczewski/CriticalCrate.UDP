namespace CriticalCrate.ReliableUdp.Channels;

public interface IChannel
{
    void Send(Packet packet);
}

public interface IReceiveChannel
{
}
