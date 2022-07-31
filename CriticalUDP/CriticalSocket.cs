using System.Collections.Concurrent;
using System.Net;

namespace CriticalCrate.UDP
{
    public class CriticalSocket
    {
        private UDPSocket _socket;
        private ConnectionManager _connectionManager;
        private ConcurrentDictionary<int, ReliableChannel> _reliableChannels;
        private ConcurrentQueue<Packet> _pendingPackets = new ConcurrentQueue<Packet>();

        public CriticalSocket(int mtu)
        {
            _socket = new UDPSocket(mtu);
            _reliableChannels = new ConcurrentDictionary<int, ReliableChannel>();
            _connectionManager = new ConnectionManager();
            _socket.OnPacketReceived += OnPacketReceived;
        }

        public void Listen(IPEndPoint endPoint)
        {
            _socket.Listen(endPoint);
        }
        
        public void Update()
        {
            foreach (var keyValue in _reliableChannels)
            {
                keyValue.Value.Update();
            }
        }

        public void Connect(IPEndPoint endPoint, Action onConnected)
        {
            _reliableChannels.TryAdd(0, CreateChannel(_socket));
            Listen(new IPEndPoint(IPAddress.Any, 0));
            _connectionManager.Connect(endPoint, onConnected);
        }

        private ReliableChannel CreateChannel(UDPSocket socket)
        {
            var channel = new ReliableChannel(_socket);
            channel.OnPacketReceived += OnReliablePacketReceived;
            return channel;
        }

        private void OnReliablePacketReceived(ReliableIncomingPacket reliablePacket)
        {
            _pendingPackets.Enqueue(reliablePacket.GetData());
        }
        
        private void OnPacketReceived(Packet packet)
        {
            if (!_connectionManager.IsConnected(packet.EndPoint, out int socketId))
                return;

            if (((PacketType)packet.Data[0] & PacketType.Unreliable) == PacketType.Unreliable)
            {
                _pendingPackets.Enqueue(packet);
            }
            else if (((PacketType)packet.Data[0] & PacketType.Reliable) == PacketType.Reliable)
            {
                if (!_reliableChannels.TryGetValue(socketId, out var channel))
                    return;
                channel.OnReceive(packet);
            }
        }
    }
}