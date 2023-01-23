using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace CriticalCrate.UDP
{
    [Flags]
    public enum PacketType
    {
        Unreliable = 1,
        Connect = 2,
        Disconnect = 4,
        Reliable = 8,
        ReliableAck = Reliable | 16,
        ReliablePacketEnd = Reliable | 32,
        Ping = 64,
        Pong = 128,
    }

//first byte
//1 0 0 0 0 0 0 0  unreliable
//0 1 0 0 0 0 0 0  connect
//0 0 1 0 0 0 0 0  disconnect
//0 0 0 1 0 0 0 0  reliable
//0 0 0 1 1 0 0 0  ack
//0 0 0 1 0 1 0 0  reliable packet end
//0 0 0 0 0 0 1 0  ping
//0 0 0 0 0 0 0 1  pong

// unreliable overhead = 2 byte {packet flags, ping seq}
// reliable overhead = 4 byte {packet flags, packet seq, packetSliceAck/Seq}
    internal class ReliableChannel : IDisposable
    {
        internal const int ReliableChannelHeaderSize = 4;
        internal const int MaxParts = short.MaxValue / 2;
        public event Action<ReliableIncomingPacket> OnPacketReceived;

        private UDPAsyncSocket _asyncSocket;
        private ConcurrentQueue<Packet> _sendQueue = new ConcurrentQueue<Packet>();
        private ConcurrentQueue<Packet> _receiveQueue = new ConcurrentQueue<Packet>();

        private ReliableOutgoingPacket _outgoingPacket;
        private ReliableIncomingPacket _currentIncomingPacket;

        private byte _packetSeq = 0;
        private readonly int _packetSendWindow;
        private DateTime _lastAckDate;
        private int _resendAfterMs = 300;

        public ReliableChannel(UDPAsyncSocket asyncSocket, int packetSendWindow = 60 * 1024)
        {
            _asyncSocket = asyncSocket;
            _packetSendWindow = packetSendWindow;
            _currentIncomingPacket = new ReliableIncomingPacket(asyncSocket.MTU);
            _currentIncomingPacket.New(0);
            _outgoingPacket = new ReliableOutgoingPacket(asyncSocket.MTU);
        }

        public void Send(EndPoint endPoint, byte[] data, int offset, int size)
        {
            var packet = new Packet(size, ArrayPool<byte>.Shared);
            packet.Assign(endPoint);
            packet.CopyFrom(data, offset, size);

            _sendQueue.Enqueue(packet);
        }

        public void UpdateRTT(long rtt)
        {
            _resendAfterMs = Math.Max(5, (int)(rtt * 1.2f));
        }

        public void Update()
        {
            while (_receiveQueue.TryDequeue(out var receivedPacket))
                HandleReceivedPacket(receivedPacket);

            int sendWindow = _packetSendWindow;
            while (_sendQueue.TryPeek(out var packet) && sendWindow > 0)
            {
                if (!_outgoingPacket.HasPackets)
                    _outgoingPacket.Split(_packetSeq++, packet.Data, 0, packet.Position, packet.EndPoint,
                        ArrayPool<byte>.Shared);

                if (_outgoingPacket.IsCompleted)
                {
                    _sendQueue.TryDequeue(out packet);
                    packet.Dispose();
                    _outgoingPacket.Reset();
                    _lastAckDate -= TimeSpan.FromMilliseconds(_resendAfterMs);
                    sendWindow = _packetSendWindow;
                    continue;
                }

                if (_lastAckDate + TimeSpan.FromMilliseconds(_resendAfterMs) > DateTime.Now)
                    return;

                _lastAckDate = DateTime.Now;
                foreach (var packetSlice in _outgoingPacket.GetNotAckedPacketSlices())
                {
                    sendWindow -= packetSlice.Position;
                    if (sendWindow < 0)
                        break;
                    _asyncSocket.Send(packetSlice);
                }
            }
        }

        public void OnReceive(Packet packet)
        {
            _receiveQueue.Enqueue(packet);
        }

        private void HandleReceivedPacket(Packet packet)
        {
            ReadHeader(packet, out var packetSeq, out var packetSliceAck, out var packetType);
            if ((packetType & PacketType.ReliableAck) == PacketType.ReliableAck)
            {
                if (!_outgoingPacket.HasPackets || _outgoingPacket.PacketSeq != packetSeq)
                    return;
                _outgoingPacket.AckSlice(packetSliceAck);
                packet.Dispose();
            }
            else if ((packetType & PacketType.Reliable) == PacketType.Reliable)
            {
                bool oldPacket = packetSeq < _currentIncomingPacket.PacketSeq ||
                                 (_currentIncomingPacket.PacketSeq == 0 && packetSeq == byte.MaxValue);
                if (oldPacket)
                {
                    _asyncSocket.Send(ReliableIncomingPacket.CreateSliceAckPacket(packet, packetSliceAck, ArrayPool<byte>.Shared));
                }
                else if (_currentIncomingPacket.PacketSeq == packetSeq)
                {
                    if(_currentIncomingPacket.ReceiveSlice(packetType, packetSeq, packetSliceAck, packet, out short ack))
                        _asyncSocket.Send(ReliableIncomingPacket.CreateSliceAckPacket(packet, ack, ArrayPool<byte>.Shared));
                    if (!_currentIncomingPacket.IsComplete()) return;
                    try
                    {
                        OnPacketReceived?.Invoke(_currentIncomingPacket);
                    }
                    catch
                    {
                        throw;
                    }
                    finally
                    {
                        _currentIncomingPacket.New((byte)(packetSeq + 1));
                    }
                }
            }
        }

        internal static void AddData(ref Packet packet, byte[] buffer, int offset, int size)
        {
            packet.CopyFrom(buffer, offset, size, packet.Position);
        }

        internal static void AddHeader(ref Packet packet, byte packetSeq, short packetPart, PacketType packetType)
        {
            packet.Data[0] = (byte)packetType;
            packet.Data[1] = packetSeq;
            MemoryMarshal.Write(packet.Data.AsSpan(2), ref packetPart);
            packet.ForcePosition(ReliableChannelHeaderSize);
        }

        internal static void ReadHeader(Packet packet, out byte packetSeq, out short ack, out PacketType packetType)
        {
            packetType = (PacketType)packet.Data[0];
            packetSeq = packet.Data[1];
            ack = BitConverter.ToInt16(packet.Data, 2);
        }

        internal static int ReadData(Packet packet, byte[] buffer, int offset)
        {
            packet.CopyTo(buffer, offset, ReliableChannel.ReliableChannelHeaderSize);
            return packet.Position - ReliableChannel.ReliableChannelHeaderSize;
        }

        public void Dispose()
        {
            _outgoingPacket.Dispose();
            _currentIncomingPacket.Dispose();
        }
    }

    public class ReliableIncomingPacket : IDisposable
    {
        private readonly Packet[] _packetSlices;
        private int _sliceCount;
        private short _sliceAck;
        public short PacketSeq { get; private set; }

        public ReliableIncomingPacket(int maxPacketSize)
        {
            _packetSlices = new Packet[ReliableChannel.MaxParts];
            PacketSeq = -1;
            _sliceCount = ReliableChannel.MaxParts;
            _sliceAck = -1;
        }

        public void New(byte seq)
        {
            Reset();
            PacketSeq = seq;
        }

        //burst enabled?
        public bool ReceiveSlice(PacketType type, byte packetSeq, short packetSliceSeq, Packet packet, out short ack)
        {
            ack = 0;
            if (packetSeq != PacketSeq)
                return false;
            if (packetSliceSeq < _sliceAck)
                return false;
            if ((type & PacketType.ReliablePacketEnd) == PacketType.ReliablePacketEnd)
                _sliceCount = packetSliceSeq + 1;
            _packetSlices[packetSliceSeq] = packet;
            while (packetSliceSeq == _sliceAck + 1)
            {
                _sliceAck++;
                while (_packetSlices[_sliceAck + 1].Position != 0)
                    _sliceAck++;
            }

            if (_sliceAck == -1)
                return false;

            ack = _sliceAck;
            return true;
        }

        public void Dispose()
        {
            Reset();
        }

        public static Packet CreateSliceAckPacket(Packet packet, short ack, ArrayPool<byte> pool)
        {
            Packet ackPacket = new Packet(ReliableChannel.ReliableChannelHeaderSize, pool);
            ackPacket.Assign(packet.EndPoint);
            ReliableChannel.ReadHeader(packet, out byte packetSeq, out short _, out PacketType type);
            ReliableChannel.AddHeader(ref ackPacket, packetSeq, ack, PacketType.ReliableAck);
            return ackPacket;
        }

        public bool IsComplete()
        {
            return _sliceAck + 1 == _sliceCount;
        }

        public void Reset()
        {
            for (int i = 0;; i++)
            {
                if (_packetSlices[i].Position == 0)
                    break;
                _packetSlices[i].Dispose();
            }
            
            _sliceCount = ReliableChannel.MaxParts;
            _sliceAck = -1;
        }

        //burst enabled?
        public Packet GetPacket()
        {
            int size = 0;
            for (int i = 0; i < _sliceCount; i++)
            {
                size += _packetSlices[i].Position - ReliableChannel.ReliableChannelHeaderSize;
            }

            Packet packet = new Packet(size, ArrayPool<byte>.Shared);
            int offset = 0;
            for (int i = 0; i < _sliceCount; i++)
            {
                offset += ReliableChannel.ReadData(_packetSlices[i], packet.Data, offset);
            }

            packet.Assign(_packetSlices[0].EndPoint);
            packet.ForcePosition(offset);
            return packet;
        }
    }

    public class ReliableOutgoingPacket : IDisposable
    {
        private readonly int _maxPacketSize;
        private readonly Packet[] _packets;
        private int _slicesCount;
        private short _sliceAck = -1;

        public ReliableOutgoingPacket(int maxPacketSize)
        {
            _maxPacketSize = maxPacketSize - ReliableChannel.ReliableChannelHeaderSize;
            _packets = new Packet[ReliableChannel.MaxParts];
            _slicesCount = 0;
            _sliceAck = -1;
            PacketSeq = 0;
        }

        public bool IsCompleted => _slicesCount == _sliceAck + 1;
        public int PacketSeq { get; private set; }
        public bool HasPackets => _slicesCount > 0;

        public void AckSlice(short sliceAck)
        {
            _sliceAck = sliceAck;
        }

        public void Split(byte packetSeq, byte[] buffer, int offset, int size, EndPoint endPoint, ArrayPool<byte> pool)
        {
            _sliceAck = -1;
            PacketSeq = packetSeq;
            int divide = size / _maxPacketSize;
            int modulo = size % _maxPacketSize;
            _slicesCount = divide + (modulo > 0 ? 1 : 0);
            if (_slicesCount >= _packets.Length)
                throw new OverflowException("Buffer is to big to send!");
            for (int i = 0; i < _slicesCount - 1; i++)
                _packets[i] = CreatePacketSlice(i, buffer, offset + (i * _maxPacketSize), _maxPacketSize, packetSeq,
                    PacketType.Reliable, endPoint, pool);
            int lastPacketSize = modulo > 0 ? modulo : _maxPacketSize;
            _packets[_slicesCount - 1] = CreatePacketSlice(_slicesCount - 1, buffer,
                offset + (_maxPacketSize * (_slicesCount - 1)), lastPacketSize, packetSeq, PacketType.ReliablePacketEnd,
                endPoint, pool);
        }

        private Packet CreatePacketSlice(int packetSliceSeq, byte[] buffer, int offset, int size, byte packetSeq, PacketType packetType,
            EndPoint endPoint, ArrayPool<byte> pool)
        {
            var packet = new Packet(ReliableChannel.ReliableChannelHeaderSize + size, pool);
            packet.Assign(endPoint);
            ReliableChannel.AddHeader(ref packet, packetSeq, (short)packetSliceSeq, packetType);
            ReliableChannel.AddData(ref packet, buffer, offset, size);
            packet.BlockSendDispose = true;
            return packet;
        }

        public Span<Packet> GetNotAckedPacketSlices()
        {
            int ack = Math.Max(0, (int)_sliceAck);
            return new Span<Packet>(_packets, ack, _slicesCount - ack);
        }

        public void Reset()
        {
            for (int i = 0; i < _slicesCount; i++)
                _packets[i].Dispose();
            _slicesCount = 0;
            _sliceAck = -1;
        }

        public void Dispose()
        {
            Reset();
        }
    }
}