using System.Net;

namespace CriticalCrate.ReliableUdp;

public interface IConnectionHandler
{
   void HandleConnection(EndPoint endPoint);
   void HandleDisconnection(EndPoint endPoint);
}


