using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public event Action<EndPoint> OnConnected;
        public event Action<EndPoint> OnDisconnected;

        public PingManager PingManager { get; private set; }
        public IConnectionManager ConnectionManager => _connectionManager;
        private BaseConnectionManager _connectionManager;

        private UDPSocket _socket;
        private UnreliableChannel _unreliableChannel;
        private Dictionary<EndPoint, ReliableChannel> _reliableChannels;
        private ConcurrentQueue<Packet> _pendingPackets;
        private ConcurrentQueue<Packet> _pendingReliable;

        private bool _isClient = false;
        private EndPoint _serverEndpoint;
        private int _timeoutMs = 10000;

        #if UNITY_EDITOR
        public CriticalSocket(int timeoutMs = 10000000) // pause and debugging
        #else
        public CriticalSocket(int timeoutMs = 10000)
        #endif
        {
            _socket = new UDPSocket();
            _timeoutMs = timeoutMs;
            PingManager = new PingManager(_socket);
            _reliableChannels = new Dictionary<EndPoint, ReliableChannel>();
            _unreliableChannel = new UnreliableChannel(_socket, PingManager);
            _pendingPackets = new ConcurrentQueue<Packet>();
            _pendingReliable = new ConcurrentQueue<Packet>();
            _socket.OnPacketReceived += OnPacketReceived;
        }

        public void Listen(IPEndPoint endPoint, int maxClients = 1)
        {
            _serverEndpoint = endPoint;
            _socket.Listen(endPoint);
            _connectionManager = new ServerConnectionManager(_timeoutMs, maxClients, _socket);
            _connectionManager.OnConnected += HandleConnected;
            _connectionManager.OnDisconnected += HandleDisconnected;
        }

        public void Listen(ushort port, int maxClients = 1)
        {
            Listen(new IPEndPoint(IPAddress.Any, port), maxClients);
        }

        public int GetLowestConnectedMTU()
        {
            return _connectionManager.GetLowestConnectedMTU();
        }

        public bool Pool(out Packet packet, out int eventsLeft)
        {
            PingManager.Update();
            _connectionManager.CheckConnectionTimeout();
            foreach (var keyValue in _reliableChannels)
            {
                var endPoint = keyValue.Key;
                keyValue.Value.UpdateRTT(PingManager.GetPing(endPoint));
                keyValue.Value.Update();
            }

            eventsLeft = 0;
            packet = default;

            if (_pendingReliable.TryDequeue(out packet))
            {
                eventsLeft = _pendingReliable.Count + _pendingPackets.Count;
                return true;
            }

            if (!_pendingPackets.TryDequeue(out packet))
            {
                return false;
            }

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

            if (!_connectionManager.IsConnected(packet.EndPoint))
                return Pool(out packet, out eventsLeft);
            _connectionManager.OnPacket(packet);

            if (((PacketType)packet.Data[0] & PacketType.Reliable) == PacketType.Reliable)
            {
                if (!_reliableChannels.TryGetValue(packet.EndPoint, out var channel))
                    return Pool(out packet, out eventsLeft);
                channel.OnReceive(packet);
                return Pool(out packet, out eventsLeft);
            }

            PingManager.OnPacketReceived(packet);
            if (((PacketType)packet.Data[0] & PacketType.Pong) == PacketType.Pong)
                return Pool(out packet, out eventsLeft);

            if (((PacketType)packet.Data[0] & PacketType.Ping) == PacketType.Ping && packet.Position == 2)
                return Pool(out packet, out eventsLeft);

            eventsLeft = _pendingPackets.Count;
            packet.CopyFrom(packet.Data, UnreliableChannel.UnreliableHeaderSize,
                packet.Position - UnreliableChannel.UnreliableHeaderSize, 0);
            return true;
        }

        public void Connect(IPEndPoint endPoint, int connectingTimeoutMs, Action<bool> onConnected)
        {
            _isClient = true;
            Listen(new IPEndPoint(IPAddress.Any, 0));
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
            
            if (!_connectionManager.IsConnected(endPoint))
                throw new NotImplementedException("Socket needs to be connected to send data!");
            
            if (isUnreliable)
            {
                if (size + UnreliableChannel.UnreliableHeaderSize >= _connectionManager.GetLowestConnectedMTU())
                    Console.WriteLine("Packet size bigger than MTU!");
                _unreliableChannel.Send(endPoint, data, offset, size);
                return;
            }
            
            _reliableChannels[endPoint].Send(endPoint, data, offset, size);
        }

        public void Send(byte[] data, int offset, int size, SendMode sendMode = SendMode.Unreliable)
        {
            if (!_isClient)
                throw new NotImplementedException("Socket in server mode need endpoint for sending!");
            Send(_serverEndpoint, data, offset, size, sendMode);
        }

        private ReliableChannel CreateChannel(UDPSocket socket, int mtu)
        {
            var channel = new ReliableChannel(socket, mtu);
            channel.OnPacketReceived += OnReliablePacketReceived;
            return channel;
        }

        private void HandleDisconnected(EndPoint endPoint)
        {
            if (_reliableChannels.Remove(endPoint, out var channel))
                channel.Dispose();
            PingManager.OnDisconnected(endPoint);
            OnDisconnected?.Invoke(endPoint);
        }

        private void HandleConnected(EndPoint endPoint)
        {
            var newChannel = CreateChannel(_socket, _connectionManager.GetMTU(endPoint));
            if (!_reliableChannels.TryAdd(endPoint, newChannel))
                newChannel.Dispose();
            PingManager.OnConnected(endPoint);
            OnConnected?.Invoke(endPoint);
        }

        private void OnReliablePacketReceived(ReliableIncomingPacket reliablePacket)
        {
            _pendingReliable.Enqueue(reliablePacket.GetResultPacket());
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