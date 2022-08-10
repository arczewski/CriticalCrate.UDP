using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CriticalCrate.UDP
{
    public delegate void OnPacketReceived(Packet packet);
    public class UDPSocket : IDisposable
    {
        public const int MaxMTU = 1500;
        public const int MinMTU = 576;
        public event OnPacketReceived OnPacketReceived;
        
        private SocketAsyncEventArgs _readEvent;
        private SocketAsyncEventArgs _writeEvent;
        private Socket _listenSocket;

        private BlockingCollection<Packet> _packets;
        private SemaphoreSlim _sendSemaphore;
        private Thread _sendThread;
        private CancellationTokenSource _sendThreadCancelation;
        

        public UDPSocket()
        {
            _sendSemaphore = new SemaphoreSlim(1, 1);
            _packets = new BlockingCollection<Packet>();
            _sendThreadCancelation = new CancellationTokenSource();
            _sendThread = new Thread(async () => ProcessSendQueue(_sendThreadCancelation.Token));
            _sendThread.Start();

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
            if((_listenSocket.AddressFamily & AddressFamily.InterNetwork) == AddressFamily.InterNetwork)
                _listenSocket.DontFragment = true;
            SetupSocketEvents();
            if (!_listenSocket.ReceiveFromAsync(_readEvent))
                ProcessRead(_readEvent);
        }

        public void Client(ushort port = 0)
        {
            Listen(new IPEndPoint(IPAddress.Any, port));
        }

        private async void ProcessSendQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _sendSemaphore.WaitAsync(cancellationToken);
                var packet = _packets.Take(cancellationToken);
                _writeEvent.SetBuffer(0, packet.Position);
                _writeEvent.RemoteEndPoint = packet.EndPoint;
               // _writeEvent.SetBuffer(new Memory<byte>(_writeEvent.Buffer, _writeEvent.Offset, packet.Position));
                packet.CopyTo(_writeEvent.Buffer, _writeEvent.Offset);
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
            _readEvent.SetBuffer(new byte[MaxMTU], 0, MaxMTU);
            _readEvent.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 0);
            _readEvent.Completed += OnIOCompleted;

            _writeEvent = new SocketAsyncEventArgs();
            _writeEvent.SetBuffer(new byte[MaxMTU], 0, MaxMTU);
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
                case SocketAsyncOperation.ReceiveFrom:
                    ProcessRead(e);
                    break;
                case SocketAsyncOperation.SendTo:
                    ProcessWrite(e);
                    break;
                default:
                    return;
            }
        }

        private void ProcessWrite(SocketAsyncEventArgs e)
        {
            _writeEvent.SetBuffer(0, MaxMTU);
            _sendSemaphore.Release();
        }

        private void ProcessRead(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                var packet = new Packet(e.BytesTransferred, ArrayPool<byte>.Shared);
                packet.Assign((IPEndPoint)e.RemoteEndPoint);
                packet.CopyFrom(e.Buffer, e.Offset, e.BytesTransferred);
                OnPacketReceived?.Invoke(packet);
                if (!_listenSocket.ReceiveFromAsync(e))
                    ProcessRead(e);
            }
        }

        public void Dispose()
        {
            _sendThreadCancelation.Cancel();
        }
    }
}
