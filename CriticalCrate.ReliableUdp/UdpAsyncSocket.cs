using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CriticalCrate.ReliableUdp
{
    public interface ISocket : IDisposable
    {
        const int Mtu = 508;
        event OnPacketReceived OnPacketReceived;
        event OnPacketSent OnPacketSent;
        void Listen(EndPoint endPoint);
        void Send(Packet packet);
    }

    public delegate void OnPacketReceived(Packet packet);

    public delegate void OnPacketSent(Packet packet);

    public sealed class UdpAsyncSocket : ISocket
    {
        public event OnPacketReceived? OnPacketReceived;
        public event OnPacketSent? OnPacketSent;

        private readonly SocketAsyncEventArgs _readEvent;
        private readonly SocketAsyncEventArgs _writeEvent;
        private readonly Socket _listenSocket;
        private readonly BlockingCollection<Packet> _packetsToBeSend;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly IPacketFactory _packetFactory;

        public UdpAsyncSocket(IPacketFactory packetFactory)
        {
            _packetFactory = packetFactory;
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _packetsToBeSend = new BlockingCollection<Packet>();
            _cancellationTokenSource = new CancellationTokenSource();
            _readEvent = new SocketAsyncEventArgs();
            _readEvent.SetBuffer(new byte[ISocket.Mtu], 0, ISocket.Mtu);
            _writeEvent = new SocketAsyncEventArgs();
        }

        public void Listen(EndPoint endPoint)
        {
            _listenSocket.Bind(endPoint);
            _readEvent.RemoteEndPoint = endPoint;
            Task.Run(() => SendPendingPackets(_cancellationTokenSource.Token));
            Task.Run(() => ReceivePendingPackets(_cancellationTokenSource.Token));
        }

        private void SendPendingPackets(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = _packetsToBeSend.Take(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return;
                _writeEvent.SetBuffer(packet.ToMemoryBuffer());
                _writeEvent.RemoteEndPoint = packet.EndPoint;
                _writeEvent.Completed += WriteCompletedAsync;
                if (_listenSocket.SendToAsync(_writeEvent)) return;
                WriteCompleted(_writeEvent);
                continue;

                void WriteCompleted(SocketAsyncEventArgs e)
                {
                    if (e.LastOperation != SocketAsyncOperation.SendTo)
                        Debug.Write($"Unrecognized socket operation - {e.LastOperation} - {e.SocketError}");
                    e.Completed -= WriteCompletedAsync;
                    OnPacketSent?.Invoke(packet);
                }

                void WriteCompletedAsync(object? sender, SocketAsyncEventArgs e)
                {
                    WriteCompleted(e);
                    SendPendingPackets(cancellationToken);
                }
            }
        }

        private void ReceivePendingPackets(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _readEvent.Completed += ReadCompletedAsync;
                if (_listenSocket.ReceiveFromAsync(_readEvent)) return;
                ReadCompleted(_readEvent);
                continue;

                void ReadCompleted(SocketAsyncEventArgs e)
                {
                    if (e.LastOperation != SocketAsyncOperation.ReceiveFrom)
                        Debug.Write($"Unrecognized socket operation - {e.LastOperation} - {e.SocketError}");
                    e.Completed -= ReadCompletedAsync;
                    var packet = _packetFactory.CreatePacket(e.RemoteEndPoint, e.Buffer, e.Offset, e.BytesTransferred);
                    OnPacketReceived?.Invoke(packet);
                }

                void ReadCompletedAsync(object? sender, SocketAsyncEventArgs e)
                {
                    ReadCompleted(e);
                    ReceivePendingPackets(cancellationToken);
                }
            }
        }

        public void Send(Packet packet)
        {
            _packetsToBeSend.Add(packet);
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _listenSocket.Dispose();
            _writeEvent.Dispose();
            _readEvent.Dispose();
            _packetsToBeSend.CompleteAdding();
            var pendingPackets = _packetsToBeSend.ToArray();
            foreach (var packet in pendingPackets)
                _packetFactory.ReturnPacket(packet);
        }
    }
}