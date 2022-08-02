using System.Buffers;
using System.Net;

namespace CriticalCrate.UDP;

public class ClientConnectionManager : IConnectionManager
{
    public event Action<int> OnConnected;
    public event Action<int> OnDisconnected;
    
    private UDPSocket _socket;
    private IPEndPoint _serverEndpoint;
    private int _timeOutMs = 10000;
    private DateTime _lastReceivedPacket;
    private Action<bool>? _onConnectAction;
    private bool _isConnected;
    private int _connectingTimeoutMs = 100;
    private int _discoveredMtu = UDPSocket.MaxMTU;

    public ClientConnectionManager(UDPSocket socket, int timeOutMs = 1000)
    {
        _socket = socket;
        _timeOutMs = timeOutMs;
    }
    public void OnConnectionPacket(Packet packet)
    {
        if (_isConnected)
            return;
        _discoveredMtu = packet.Position;
        Console.WriteLine($"Discovered MTU: {_discoveredMtu}");
        _isConnected = true;
        _lastReceivedPacket = DateTime.Now;
        _onConnectAction?.Invoke(true);
        OnConnected?.Invoke(0);
        _onConnectAction = null;
    }

    public void OnDisconnectionPacket(EndPoint packet)
    {
        if (!_isConnected)
            return;
        _isConnected = false;
        OnDisconnected?.Invoke(0);
    }

    public void OnPacket(Packet packet)
    {
        _lastReceivedPacket = DateTime.Now;
    }

    public void CheckConnectionTimeout()
    {
        if (!_isConnected)
        {
            if (_lastReceivedPacket.AddMilliseconds(_connectingTimeoutMs) >= DateTime.Now) return;
            if (_discoveredMtu > UDPSocket.MinMTU)
            {
                _discoveredMtu -= 256;
                Connect(_serverEndpoint, _connectingTimeoutMs, _onConnectAction);
                return;
            }
            _onConnectAction = null;
            _isConnected = false;
            _onConnectAction?.Invoke(false);
            return;
        }
        
        if (_lastReceivedPacket.AddMilliseconds(_timeOutMs) >= DateTime.Now) return;
        _socket.Send(ServerConnectionManager.CreateConnectionPacket(_serverEndpoint, PacketType.Disconnect));
        OnDisconnectionPacket(_serverEndpoint);
    }

    public bool IsConnected(EndPoint endPoint, out int socketId)
    {
        socketId = 0;
        return _isConnected;
    }

    public int GetLowestConnectedMTU()
    {
        return _discoveredMtu;
    }

    public int GetMTU(int socketId)
    {
        return _discoveredMtu;
    }

    public void Connect(IPEndPoint endPoint, int connectTimeoutMs, Action<bool> onConnected)
    {
        _onConnectAction = onConnected;
        _connectingTimeoutMs = connectTimeoutMs;
        _lastReceivedPacket = DateTime.Now;
        _serverEndpoint = endPoint;
        _socket.Send(ServerConnectionManager.CreateConnectionPacket(endPoint, PacketType.Connect, _discoveredMtu));
    }
}

public interface IConnectionManager
{
     event Action<int> OnConnected;
     event Action<int> OnDisconnected;
     void OnConnectionPacket(Packet packet);
     void OnDisconnectionPacket(EndPoint packet);
     void OnPacket(Packet packet);
     void CheckConnectionTimeout();
     bool IsConnected(EndPoint endPoint, out int socketId);
     int GetLowestConnectedMTU();
     int GetMTU(int socketId);
}

public class ServerConnectionManager : IConnectionManager
{
    public event Action<int> OnConnected;
    public event Action<int> OnDisconnected;

    private int _maxConnection;
    private UDPSocket _socket;
    private Dictionary<EndPoint, int> _endpointToId = new Dictionary<EndPoint, int>();
    private Dictionary<int, EndPoint> _idToEndpoint = new Dictionary<int, EndPoint>();
    private Dictionary<EndPoint, DateTime> _lastReceivedPacket = new Dictionary<EndPoint, DateTime>();
    private Dictionary<EndPoint, int> _mtu = new Dictionary<EndPoint, int>();
    private List<EndPoint> _endPointsToDisconnect = new List<EndPoint>();
    private int _timeoutMs;

    private int _lowestClientMtu = UDPSocket.MinMTU;
    
    private int _nextSocketId = int.MinValue;

    public ServerConnectionManager(int timeoutMs, int maxConnection, UDPSocket socket)
    {
        _maxConnection = maxConnection;
        _socket = socket;
        _timeoutMs = timeoutMs;
    }

    public void CheckConnectionTimeout()
    {
        _endPointsToDisconnect.Clear();
        foreach (var keyValue in _lastReceivedPacket)
        {
            if(keyValue.Value.AddMilliseconds(_timeoutMs) < DateTime.Now)
                _endPointsToDisconnect.Add(keyValue.Key);
        }
        foreach (var endpoint in _endPointsToDisconnect)
            OnDisconnectionPacket(endpoint);
    }

    public bool IsConnected(EndPoint endPoint, out int socketId)
    {
        return _endpointToId.TryGetValue(endPoint, out socketId);
    }

    public int GetLowestConnectedMTU()
    {
        return _lowestClientMtu;
    }

    public int GetMTU(int socketId)
    {
        if (!_idToEndpoint.TryGetValue(socketId, out var endPoint))
            return _lowestClientMtu;
        if (!_mtu.TryGetValue(endPoint, out int mtu))
            return _lowestClientMtu;
        return mtu;
    }

    public bool TryGetEndPoint(int socketId, out EndPoint endPoint)
    {
        return _idToEndpoint.TryGetValue(socketId, out endPoint);
    }

    public void OnConnectionPacket(Packet packet)
    {
        if (_endpointToId.TryGetValue(packet.EndPoint, out var socketId))
        {
            _socket.Send(CreateConnectionPacket(packet.EndPoint, PacketType.Connect, packet.Position));
            return;
        }

        if (_lowestClientMtu > packet.Position)
            _lowestClientMtu = packet.Position;
        
        socketId = _nextSocketId++;
        _mtu.Add(packet.EndPoint, packet.Position);
        _endpointToId.Add(packet.EndPoint, socketId);
        _idToEndpoint.Add(socketId, packet.EndPoint);
        _lastReceivedPacket.Add(packet.EndPoint, DateTime.Now);
        _socket.Send(CreateConnectionPacket(packet.EndPoint, PacketType.Connect, packet.Position));
        OnConnected?.Invoke(socketId);
    }

    internal static Packet CreateConnectionPacket(EndPoint endPoint, PacketType type, int mtu = 1)
    {
        var packet = new Packet(mtu, ArrayPool<byte>.Shared);
        packet.Assign(endPoint);
        packet.Data[0] = (byte)type;
        packet.ForcePosition(mtu);
        return packet;
    }

    public void OnDisconnectionPacket(EndPoint endpoint)
    {
        if (!_endpointToId.TryGetValue(endpoint, out var socketId))
            return;
        _endpointToId.Remove(endpoint);
        _idToEndpoint.Remove(socketId);
        _lastReceivedPacket.Remove(endpoint);
        _mtu.Remove(endpoint);
        _socket.Send(CreateConnectionPacket(endpoint, PacketType.Disconnect));
        OnDisconnected?.Invoke(socketId);
    }

    public void OnPacket(Packet packet)
    {
        _lastReceivedPacket[packet.EndPoint] = DateTime.Now;
    }
}