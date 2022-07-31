using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace CriticalCrate.UDP
{
    public delegate void OnPacketReceived(Packet packet);
    public class UDPSocket : IDisposable
    {
        public event OnPacketReceived OnPacketReceived;

        public int Mtu { get; set; }
        private SocketAsyncEventArgs _readEvent;
        private SocketAsyncEventArgs _writeEvent;
        private Socket _listenSocket;

        private BlockingCollection<Packet> _packets = new BlockingCollection<Packet>();
        private SemaphoreSlim _sendSemaphore;
        private Thread _sendThread;
        private CancellationTokenSource _sendThreadCancelation;

        public UDPSocket(int mtu)
        {
            Mtu = mtu;
            _sendSemaphore = new SemaphoreSlim(1, 1);
            _packets = new BlockingCollection<Packet>();
            _sendThreadCancelation = new CancellationTokenSource();
            _sendThread = new Thread(async () => ProcessSendQueue(_sendThreadCancelation.Token));
            _sendThread.Start();

            _listenSocket?.Dispose();
            _listenSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
        }

        public void Listen(ushort port)
        {
            Listen(new IPEndPoint(IPAddress.Any, port));
        }
        
        public void Listen(IPEndPoint endPoint)
        {
            _listenSocket.Bind(endPoint);
            SetupSocketEvents();
            if (!_listenSocket.ReceiveMessageFromAsync(_readEvent))
                ProcessRead(_readEvent);
        }

        public void Client()
        {
            Listen(new IPEndPoint(IPAddress.Any, 0));
        }

        private async void ProcessSendQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = _packets.Take(cancellationToken);
                await _sendSemaphore.WaitAsync(cancellationToken);
                packet.CopyTo(_writeEvent.Buffer, _writeEvent.Offset);
                _writeEvent.SetBuffer(0, packet.Data.Length);
                _writeEvent.RemoteEndPoint = packet.EndPoint;
                packet.Dispose();
                if (!_listenSocket.SendToAsync(_writeEvent))
                    ProcessWrite(_writeEvent);
            }

            _readEvent.Dispose();
            _writeEvent.Dispose();
            _listenSocket.Dispose();
            _packets.Dispose();
            _sendSemaphore.Dispose();
        }

        private void SetupSocketEvents()
        {
            _readEvent = new SocketAsyncEventArgs();
            _readEvent.SetBuffer(new byte[Mtu], 0, Mtu);
            _readEvent.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 0);
            _readEvent.Completed += OnIOCompleted;

            _writeEvent = new SocketAsyncEventArgs();
            _writeEvent.SetBuffer(new byte[Mtu], 0, Mtu);
            _writeEvent.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 0);
            _writeEvent.Completed += OnIOCompleted;
        }

        public void Send(Packet packet)
        {
            _packets.Add(packet);
        }

        private void OnIOCompleted(object? sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.ReceiveMessageFrom:
                    ProcessRead(e);
                    break;
                case SocketAsyncOperation.SendTo:
                    ProcessWrite(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }

        private void ProcessWrite(SocketAsyncEventArgs e)
        {
            _writeEvent.SetBuffer(0, Mtu);
            _sendSemaphore.Release();
        }

        private void ProcessRead(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var packet = new Packet(e.BytesTransferred);
                packet.Assign((IPEndPoint)e.RemoteEndPoint);
                packet.CopyFrom(e.Buffer, e.Offset, e.BytesTransferred);
                OnPacketReceived?.Invoke(packet);
                if (!_listenSocket.ReceiveMessageFromAsync(e))
                    ProcessRead(e);
            }
        }

        public void Dispose()
        {
            _sendThreadCancelation.Cancel();
        }
    }
}
