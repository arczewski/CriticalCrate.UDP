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

    public class CriticalSocket : IDisposable
    {
        public event Action<int> OnConnected;
        public event Action<int> OnDisconnected;
        
        private UDPSocket _socket;
        private IConnectionManager _connectionManager;
        private Dictionary<int, ReliableChannel> _reliableChannels;
        private ConcurrentQueue<Packet> _pendingPackets;

        private bool _isClient = false;
        private EndPoint _serverEndpoint;
        private int _timeoutMs = 10000;

        public CriticalSocket(int mtu, int timeoutMs = 10000)
        {
            _socket = new UDPSocket(mtu);
            _timeoutMs = timeoutMs;
            _reliableChannels = new Dictionary<int, ReliableChannel>();
            _pendingPackets = new ConcurrentQueue<Packet>();
            _socket.OnPacketReceived += OnPacketReceived;
        }

        public void Listen(IPEndPoint endPoint, int maxClients = 1, int timeoutMilliseconds = 10000)
        {
            _serverEndpoint = endPoint;
            _socket.Listen(endPoint);
            _connectionManager = new ServerConnectionManager(timeoutMilliseconds, maxClients, _socket);
            _connectionManager.OnConnected += HandleConnected;
            _connectionManager.OnDisconnected += HandleDisconnected;
        }

        public void Listen(ushort port, int maxClients = 1, int timeoutMilliseconds = 10000)
        {
            Listen(new IPEndPoint(IPAddress.Any, port), maxClients, timeoutMilliseconds);
        }

        public bool Pool(out Packet packet, out int eventsLeft)
        {
            _connectionManager.CheckConnectionTimeout();
            foreach (var keyValue in _reliableChannels)
                keyValue.Value.Update();
            
            eventsLeft = 0;
            packet = default;
            if (!_pendingPackets.TryDequeue(out packet))
                return false;
            
            if (((PacketType)packet.Data[0] & PacketType.Connect) == PacketType.Connect)
            {
                _connectionManager.OnConnectionPacket(packet);
                packet.Dispose();
                return Pool(out packet, out eventsLeft);
            }

            if (((PacketType)packet.Data[0] & PacketType.Disconnect) == PacketType.Disconnect)
            {
                _connectionManager.OnDisconnectionPacket(packet.EndPoint);
                packet.Dispose();
                return Pool(out packet, out eventsLeft);
            }

            if (!_connectionManager.IsConnected(packet.EndPoint, out int socketId))
                return Pool(out packet, out eventsLeft);

            if (((PacketType)packet.Data[0] & PacketType.Reliable) == PacketType.Reliable)
            {
                if (!_reliableChannels.TryGetValue(socketId, out var channel))
                    return Pool(out packet, out eventsLeft);
                channel.OnReceive(packet);
                return Pool(out packet, out eventsLeft);
            }
            
            eventsLeft = _pendingPackets.Count;
            return true;
        }

        public void Connect(IPEndPoint endPoint, int connectingTimeoutMs, Action<bool> onConnected)
        {
            _isClient = true;
            Listen(new IPEndPoint(IPAddress.Any, 6000));
            _serverEndpoint = endPoint;
            _connectionManager = new ClientConnectionManager(_socket, _timeoutMs);
            _connectionManager.OnConnected += HandleConnected;
            _connectionManager.OnDisconnected += HandleDisconnected;
            ((ClientConnectionManager)_connectionManager).Connect(endPoint, connectingTimeoutMs, onConnected);
        }

        public void Send(EndPoint endPoint, byte[] data, int offset, int size, SendMode sendMode = SendMode.Unreliable)
        {
            bool isUnreliable = sendMode == SendMode.Unreliable;
            int socketId = 0;
            if (isUnreliable && size > _socket.Mtu)
                throw new ArgumentException(
                    $"Unreliable packet is to big - using {size} bytes of {_socket.Mtu} available");
            if (isUnreliable)
            {
                var packet = new Packet(size, ArrayPool<byte>.Shared);
                packet.CopyFrom(data, offset, size);
                packet.Assign(endPoint);
                _socket.Send(packet);
                return;
            }
            
            if (!_connectionManager.IsConnected(endPoint, out socketId))
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

        private void HandleDisconnected(int socketId)
        {
            if (_reliableChannels.Remove(socketId, out var channel))
                channel.Dispose();
            OnDisconnected?.Invoke(socketId);
        }

        private void HandleConnected(int socketId)
        {
            var newChannel = CreateChannel(_socket);
            if (!_reliableChannels.TryAdd(socketId, newChannel))
                newChannel.Dispose();
            OnConnected?.Invoke(socketId);
        }

        private void OnReliablePacketReceived(ReliableIncomingPacket reliablePacket)
        {
            _pendingPackets.Enqueue(reliablePacket.GetData());
        }

        private void OnPacketReceived(Packet packet)
        {
            _pendingPackets.Enqueue(packet);
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}