namespace CriticalCrate.UDP
{
    public class ReliableChannel
    {
        private UDPSocket _socket;
        
        public ReliableChannel(UDPSocket socket)
        {
            _socket = socket;
        }
    }
}