using System.Buffers;
using System.Collections.Concurrent;
using System.Net;

namespace CriticalCrate.UDP
{
    public enum SendMode
    {
        Unreliable = 0,
        Reliable = 1,
    }

    public class CriticalSocket
    {
        private UDPSocket _socket;
        private IConnectionManager _connectionManager;
        private ConcurrentDictionary<int, ReliableChannel> _reliableChannels;
        private ConcurrentQueue<Packet> _pendingPackets;

        private bool _isClient = false;
        private EndPoint _serverEndpoint;
        private int _timeoutMs = 10000;

        public CriticalSocket(int mtu, int timeoutMs = 10000)
        {
            _socket = new UDPSocket(mtu);
            _timeoutMs = timeoutMs;
            _reliableChannels = new ConcurrentDictionary<int, ReliableChannel>();
            _pendingPackets = new ConcurrentQueue<Packet>();
            _socket.OnPacketReceived += OnPacketReceived;
        }

        public void Listen(IPEndPoint endPoint, int maxClients = 1, int timeoutMilliseconds = 10000)
        {
            _serverEndpoint = endPoint;
            _socket.Listen(endPoint);
            _connectionManager = new ServerConnectionManager(timeoutMilliseconds, maxClients, _socket);
            _connectionManager.OnConnected += OnConnected;
            _connectionManager.OnDisconnected += OnDisconnected;
        }

        public int Pool(out Packet packet)
        {
            _connectionManager.CheckConnectionTimeout();
            foreach (var keyValue in _reliableChannels)
            {
                keyValue.Value.Update();
            }

            if (!_pendingPackets.TryDequeue(out packet))
                return 0;
            return _pendingPackets.Count;
        }

        public void Connect(IPEndPoint endPoint, int connectingTimeoutMs, Action<bool> onConnected)
        {
            _isClient = true;
            _serverEndpoint = endPoint;
            Listen(new IPEndPoint(IPAddress.Any, 0));
            _connectionManager = new ClientConnectionManager(_socket, _timeoutMs);
            _connectionManager.OnConnected += OnConnected;
            _connectionManager.OnDisconnected += OnDisconnected;
            ((ClientConnectionManager)_connectionManager).Connect(endPoint, connectingTimeoutMs, onConnected);
        }

        public void Send(EndPoint endPoint, byte[] data, int offset, int size, SendMode sendMode = SendMode.Unreliable)
        {
            bool isUnreliable = sendMode == SendMode.Unreliable;
            int socketId = 0;
            if (isUnreliable && size > _socket.Mtu)
                throw new ArgumentException(
                    $"Unreliable packet is to big - using {{size}} bytes of {_socket.Mtu} available");
            if (isUnreliable)
            {
                var packet = new Packet(size, ArrayPool<byte>.Shared);
                packet.CopyFrom(data, offset, size);
                packet.Assign(endPoint);
                _socket.Send(packet);
            }
            else if (!_connectionManager.IsConnected(endPoint, out socketId))
                throw new NotImplementedException("Socket needs to be connected to send reliable data!");
            _reliableChannels[socketId].Send(endPoint, data, offset, size);
        }

        public void Send(byte[] data, int offset, int size, SendMode sendMode = SendMode.Unreliable)
        {
            if (!_isClient)
                throw new NotImplementedException("Socket in server mode need endpoint for sending!");
            Send(_serverEndpoint, data, offset, size, sendMode);
        }

        private ReliableChannel CreateChannel(UDPSocket socket)
        {
            var channel = new ReliableChannel(_socket);
            channel.OnPacketReceived += OnReliablePacketReceived;
            return channel;
        }

        private void OnDisconnected(int socketId)
        {
            if (_reliableChannels.Remove(socketId, out var channel))
                channel.Dispose();
        }

        private void OnConnected(int socketId)
        {
            var newChannel = CreateChannel(_socket);
            if (!_reliableChannels.TryAdd(socketId, newChannel))
                newChannel.Dispose();
        }

        private void OnReliablePacketReceived(ReliableIncomingPacket reliablePacket)
        {
            _pendingPackets.Enqueue(reliablePacket.GetData());
        }

        private void OnPacketReceived(Packet packet)
        {
            if (((PacketType)packet.Data[0] & PacketType.Connect) == PacketType.Connect)
            {
                _connectionManager.OnConnectionPacket(packet);
                packet.Dispose();
                return;
            }

            if (((PacketType)packet.Data[0] & PacketType.Disconnect) == PacketType.Disconnect)
            {
                _connectionManager.OnDisconnectionPacket(packet.EndPoint);
                packet.Dispose();
                return;
            }

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