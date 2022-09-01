using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;

namespace CriticalCrate.UDP
{
    public class ClientConnectionManager : BaseConnectionManager
    {
        public override event Action<EndPoint> OnConnected;
        public override event Action<EndPoint> OnDisconnected;

        private UDPSocket _socket;
        private IPEndPoint _serverEndpoint;
        private int _timeOutMs = 10000;
        private DateTime _lastReceivedPacket;
        private Action<bool>? _onConnectAction;
        private bool _isConnected;
        private int _connectingTimeoutMs = 100;

        public ClientConnectionManager(UDPSocket socket, int timeOutMs = 1000)
        {
            _socket = socket;
            _timeOutMs = timeOutMs;
        }

        internal override void OnConnectionPacket(Packet packet)
        {
            if (_isConnected)
                return;
            _isConnected = true;
            _lastReceivedPacket = DateTime.Now;
            OnConnected?.Invoke(packet.EndPoint);
			 _onConnectAction?.Invoke(true);
            _onConnectAction = null;
        }

        internal override void OnDisconnectionPacket(EndPoint endPoint)
        {
            if (!_isConnected)
                return;
            _isConnected = false;
            OnDisconnected?.Invoke(endPoint);
        }

        internal override void OnPacket(Packet packet)
        {
            _lastReceivedPacket = DateTime.Now;
        }

        internal override void CheckConnectionTimeout()
        {
            if (!_isConnected)
            {
                if (_lastReceivedPacket.AddMilliseconds(_connectingTimeoutMs) >= DateTime.Now) return;
                _onConnectAction = null;
                _isConnected = false;
                _onConnectAction?.Invoke(false);
                return;
            }

            if (_lastReceivedPacket.AddMilliseconds(_timeOutMs) >= DateTime.Now) return;
            _socket.Send(ServerConnectionManager.CreateConnectionPacket(_serverEndpoint, PacketType.Disconnect));
            OnDisconnectionPacket(_serverEndpoint);
        }

        public override bool IsConnected(EndPoint endPoint)
        {
            return _isConnected;
        }

        public void Connect(IPEndPoint endPoint, int connectTimeoutMs, Action<bool> onConnected)
        {
            _onConnectAction = onConnected;
            _connectingTimeoutMs = connectTimeoutMs;
            _lastReceivedPacket = DateTime.Now;
            _serverEndpoint = endPoint;
            _socket.Send(ServerConnectionManager.CreateConnectionPacket(endPoint, PacketType.Connect, _socket.MTU));
        }
    }

    public interface IConnectionManager
    {
        event Action<EndPoint> OnConnected;
        event Action<EndPoint> OnDisconnected;
        bool IsConnected(EndPoint endPoint);
    }

    public abstract class BaseConnectionManager : IConnectionManager
    {
        public abstract event Action<EndPoint>? OnConnected;
        public abstract event Action<EndPoint>? OnDisconnected;
        public abstract bool IsConnected(EndPoint endPoint);
        internal abstract void OnConnectionPacket(Packet packet);
        internal abstract void OnDisconnectionPacket(EndPoint packet);
        internal abstract void OnPacket(Packet packet);
        internal abstract void CheckConnectionTimeout();
    }

    public class ServerConnectionManager : BaseConnectionManager
    {
        public override event Action<EndPoint> OnConnected;
        public override event Action<EndPoint> OnDisconnected;

        private int _maxConnection;
        private UDPSocket _socket;
        private Dictionary<EndPoint, DateTime> _lastReceivedPacket = new Dictionary<EndPoint, DateTime>();
        private List<EndPoint> _endPointsToDisconnect = new List<EndPoint>();
        private int _timeoutMs;

        public ServerConnectionManager(int timeoutMs, int maxConnection, UDPSocket socket)
        {
            _maxConnection = maxConnection;
            _socket = socket;
            _timeoutMs = timeoutMs;
        }

        internal override void CheckConnectionTimeout()
        {
            _endPointsToDisconnect.Clear();
            foreach (var keyValue in _lastReceivedPacket)
            {
                if (keyValue.Value.AddMilliseconds(_timeoutMs) < DateTime.Now)
                    _endPointsToDisconnect.Add(keyValue.Key);
            }

            foreach (var endpoint in _endPointsToDisconnect)
                OnDisconnectionPacket(endpoint);
        }

        public override bool IsConnected(EndPoint endPoint)
        {
            return _lastReceivedPacket.ContainsKey(endPoint);
        }

        internal override void OnConnectionPacket(Packet packet)
        {
            if (_lastReceivedPacket.TryGetValue(packet.EndPoint, out var lastPacketTime))
            {
                _socket.Send(CreateConnectionPacket(packet.EndPoint, PacketType.Connect, packet.Position));
                return;
            }
            _lastReceivedPacket.Add(packet.EndPoint, DateTime.Now);
            _socket.Send(CreateConnectionPacket(packet.EndPoint, PacketType.Connect, packet.Position));
            OnConnected?.Invoke(packet.EndPoint);
        }

        internal static Packet CreateConnectionPacket(EndPoint endPoint, PacketType type, int mtu = 1)
        {
            var packet = new Packet(mtu, ArrayPool<byte>.Shared);
            packet.Assign(endPoint);
            packet.Data[0] = (byte)type;
            packet.ForcePosition(mtu);
            return packet;
        }

        internal override void OnDisconnectionPacket(EndPoint endpoint)
        {
            if (!_lastReceivedPacket.TryGetValue(endpoint, out var lastPacketTIme))
                return;
            _socket.Send(CreateConnectionPacket(endpoint, PacketType.Disconnect));
            _lastReceivedPacket.Remove(endpoint);
            OnDisconnected?.Invoke(endpoint);
        }

        internal override void OnPacket(Packet packet)
        {
            _lastReceivedPacket[packet.EndPoint] = DateTime.Now;
        }
    }
}