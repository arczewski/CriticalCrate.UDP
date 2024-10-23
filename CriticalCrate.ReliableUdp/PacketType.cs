namespace CriticalCrate.ReliableUdp;

[Flags]
public enum PacketType
{
    Unreliable = 1,
    Connect = 2,
    Disconnect = 4,
    Reliable = 8, 
    Ack = 16,
    Ping = 64,
    
    PingAck = Ping | Ack,
    ReliableAck = Reliable | Ack,
    ServerFull = Connect | Disconnect,
}