
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CriticalCrate.UDP
{
    public class UDPSocket : ISocket, IDisposable
    {
        public int MTU => 508; //https://stackoverflow.com/questions/1098897/what-is-the-largest-safe-udp-packet-size-on-the-internet

        public event OnPacketReceived OnPacketReceived;
        
        private readonly Socket _listenSocket;

        private readonly BlockingCollection<Packet> _sendQueue;
        private Thread _sendThread;
        private Thread _readThread;
        private CancellationTokenSource _cancellationTokenSource;

        public UDPSocket()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _sendQueue = new BlockingCollection<Packet>();
            _sendThread = new Thread(()=>ProcessSendQueue(_cancellationTokenSource.Token));
            _sendThread.Start();

            _readThread = new Thread(()=>ReadSocketData(_cancellationTokenSource.Token));
            _readThread.Start();

            _listenSocket?.Dispose();
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        public void Listen(ushort port)
        {
            Listen(new IPEndPoint(IPAddress.Any, port));
        }

        public void Listen(IPEndPoint endPoint)
        {
            _listenSocket.Bind(endPoint);
            _listenSocket.Blocking = true;
            if ((_listenSocket.AddressFamily & AddressFamily.InterNetwork) == AddressFamily.InterNetwork)
                _listenSocket.DontFragment = true;
        }

        private void ProcessSendQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _sendQueue.TryTake(out var packet))
            {
                _listenSocket.SendTo(packet.Data, 0, packet.Position, SocketFlags.None, packet.EndPoint);
                if(!packet.BlockSendDispose)
                    packet.Dispose();
            }
            _sendQueue.Dispose();
        }
        
        private void ReadSocketData(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[MTU];
            EndPoint ipEndPoint = new IPEndPoint(IPAddress.Any, 0); // pool - allocation
            while (!cancellationToken.IsCancellationRequested) //add cancellation token
            {
                int bytesCount = _listenSocket.ReceiveFrom(buffer, ref ipEndPoint);
                if (cancellationToken.IsCancellationRequested)
                    break;
                var packet = new Packet(bytesCount, ArrayPool<byte>.Shared);
                packet.Assign((IPEndPoint)ipEndPoint);
                packet.CopyFrom(buffer, 0, bytesCount);
                OnPacketReceived?.Invoke(packet);
            }
        }

        public void Send(Packet packet)
        {
            _sendQueue.Add(packet);
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _listenSocket.Shutdown(SocketShutdown.Both);
            _sendQueue.CompleteAdding();
        }
    }
}