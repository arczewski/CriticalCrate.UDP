using System.Net;

namespace CriticalCrate.UDP;

public class ConnectionManager
{
    public bool IsConnected(EndPoint endPoint, out int socketId)
    {
        socketId = 1;
        return true;
    }

    public bool TryGetEndPoint(int socketId, out EndPoint endPoint)
    {
        endPoint = new IPEndPoint(IPAddress.Any, 5000);
        return true;
    }

    public void Connect(EndPoint serverEndpoint, Action onConnected)
    {
        throw new NotImplementedException();
    }
}