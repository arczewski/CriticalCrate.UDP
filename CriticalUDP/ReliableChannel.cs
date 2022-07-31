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
        Unreliable = 0,
        Connect = 1,
        Disconnect = 2,
        Reliable = 4,
        ReliableAck = Reliable | 8,
        ReliablePacketEnd = Reliable | 16,
        Ping = 32
    }

//first byte
//0 0 0 0 0 0 0 0 unreliable
//1 0 0 0 0 0 0 0 connect
//0 1 0 0 0 0 0 0 disconnect
//0 0 1 0 0 0 0 0 reliable
//0 0 1 1 0 0 0 0 ack
//0 0 1 0 1 0 0 0 reliable packet end
//0 0 0 0 0 1 0 0 ping
//
// for reliable channel:
// 1 bytes for seq - for full packet
// 2 bytes for ack/seq  - for parts
// 1 byte for ping - optional
// so unreliable overhead = 1 byte / 2 with ping
// reliable overhead = 4 byte / 5 with ping
    internal class ReliableChannel
    {
        public const int ReliableChannelHeaderSize = 4;

        public event Action<ReliableIncomingPacket> OnPacketReceived;

        private UDPSocket _socket;
        private ConcurrentQueue<Packet> _sendQueue = new ConcurrentQueue<Packet>();
        private ConcurrentQueue<Packet> _receiveQueue = new ConcurrentQueue<Packet>();

        private ReliableOutgoingPacket _outgoingPacket;
        private ReliableIncomingPacket _incomingPacket;

        private ArrayPool<byte> _arrayPool;

        private byte _sendSeq = 0;
        private readonly int _packetSendWindow;
        private DateTime _lastAckDate;
        private int _resendAfterMs = 100;
        private int _loopSequenceThreshold = 16;

        public ReliableChannel(UDPSocket socket, int packetSendWindow = 60 * 1024)
        {
            _arrayPool = ArrayPool<byte>.Shared;
            _socket = socket;
            _packetSendWindow = packetSendWindow;
            _incomingPacket = new ReliableIncomingPacket(_socket.Mtu);
            _outgoingPacket = new ReliableOutgoingPacket(_socket.Mtu);
        }

        public void Send(EndPoint endPoint, byte[] data, int offset, int size)
        {
            var packet = new Packet(size, _arrayPool);
            packet.Assign(endPoint);
            packet.CopyFrom(data, offset, size);
            _sendQueue.Enqueue(packet);
        }

        /*public void UpdateRTT(int rtt) // TODO after ping measurements is enabled
        {
            _resendAfterMs = (int)(rtt * 1.5f);
        }*/

        public void Update()
        {
            while (_receiveQueue.TryDequeue(out var receivedPacket))
                HandleReceivedPacket(receivedPacket);

            int sendWindow = _packetSendWindow;
            while (_sendQueue.TryPeek(out var bigPacket) && sendWindow > 0)
            {
                if (!_outgoingPacket.HasPackets)
                    _outgoingPacket.Split(_sendSeq++, bigPacket.Data, 0, bigPacket.Position, bigPacket.EndPoint,
                        _arrayPool);

                if (_outgoingPacket.IsCompleted)
                {
                    _sendQueue.TryDequeue(out bigPacket);
                    bigPacket.Dispose();
                    _outgoingPacket.Reset();
                    _lastAckDate = _lastAckDate - TimeSpan.FromMilliseconds(_resendAfterMs);
                    continue;
                }

                if (_lastAckDate + TimeSpan.FromMilliseconds(_resendAfterMs) > DateTime.Now)
                    return;

                _lastAckDate = DateTime.Now;

                foreach (var packet in _outgoingPacket.GetNotAckedPackets())
                {
                    sendWindow -= packet.Position;
                    if (sendWindow < 0)
                        break;
                    _socket.Send(packet);
                }
            }
        }

        public void OnReceive(Packet packet)
        {
            _receiveQueue.Enqueue(packet);
        }

        private void HandleReceivedPacket(Packet packet)
        {
            ReadHeader(packet, out var seq, out var ack, out var packetType);
            if ((packetType & PacketType.ReliableAck) == PacketType.ReliableAck)
            {
                if (!_outgoingPacket.HasPackets || _outgoingPacket.Seq != seq)
                    return;
                _outgoingPacket.Ack(ack);
                packet.Dispose();
            }
            else if ((packetType & PacketType.Reliable) == PacketType.Reliable)
            {
                bool oldPacket = _incomingPacket.Seq - seq < _loopSequenceThreshold && _incomingPacket.Seq != seq;
                if ((_incomingPacket.Seq != -1 || _incomingPacket.IsComplete()) &&
                    (_incomingPacket.Seq < seq || !oldPacket))
                {
                    _incomingPacket.New(seq);
                }

                if (_incomingPacket.Seq == seq)
                {
                    _incomingPacket.Receive(packetType, seq, ack, packet);
                    if (!_incomingPacket.IsComplete()) return;
                    _socket.Send(ReliableIncomingPacket.CreateAckPacket(packet, _arrayPool));
                    _incomingPacket.Reset();
                    OnPacketReceived?.Invoke(_incomingPacket);
                }
                else if (_incomingPacket.Seq == -1 || oldPacket)
                {
                    _socket.Send(ReliableIncomingPacket.CreateAckPacket(packet, _arrayPool));
                }
            }
        }

        internal static void AddData(ref Packet packet, byte[] buffer, int offset, int size)
        {
            packet.CopyFrom(buffer, offset, size, ReliableChannelHeaderSize);
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
    }

    public class ReliableIncomingPacket : IDisposable
    {
        private const int _maxParts = short.MaxValue / 2;

        private readonly int _maxPacketSize;
        private readonly Packet[] _packets;
        private int _partsCount;
        private short _ack;
        public short Seq { get; private set; }

        public ReliableIncomingPacket(int maxPacketSize)
        {
            _maxPacketSize = maxPacketSize - ReliableChannel.ReliableChannelHeaderSize;
            _packets = new Packet[_maxParts];
            Seq = -1;
        }

        public void New(byte seq)
        {
            Seq = seq;
        }

        //burst enabled?
        public void Receive(PacketType type, byte messageSeq, short packetSeq, Packet packet)
        {
            if (messageSeq != Seq)
                return;
            if (packetSeq < _ack)
                return;
            if ((type & PacketType.ReliablePacketEnd) == PacketType.ReliablePacketEnd)
                _partsCount = packetSeq + 1;
            _packets[packetSeq] = packet;
            var seq = packetSeq;
            while (seq == _ack)
            {
                _ack++;
                seq = _packets.Length > _ack && _packets[_ack].EndPoint != null ? _ack : seq;
            }
        }

        public void Dispose()
        {
            Reset();
        }

        public static Packet CreateAckPacket(Packet packet, ArrayPool<byte> pool)
        {
            Packet ackPacket = new Packet(ReliableChannel.ReliableChannelHeaderSize, pool);
            ackPacket.Assign(packet.EndPoint);
            ReliableChannel.ReadHeader(packet, out byte packetSeq, out short ack, out PacketType type);
            ReliableChannel.AddHeader(ref ackPacket, packetSeq, ack, PacketType.ReliableAck);
            return ackPacket;
        }

        public bool IsComplete()
        {
            return _ack == _partsCount;
        }

        public void Reset()
        {
            for (int i = 0; i < _partsCount; i++)
            {
                _packets[i].Dispose();
            }

            _partsCount = 0;
            Seq = -1;
            _ack = 0;
        }

        public bool TryGetAckPacket(out Packet packet)
        {
            if (_ack < 1)
            {
                packet = default;
                return false;
            }

            packet = _packets[_ack - 1];
            return true;
        }

        public byte[] GetData()
        {
            int size = 0;
            for (int i = 0; i < _partsCount; i++)
            {
                size += _packets[i].Position - ReliableChannel.ReliableChannelHeaderSize;
            }

            byte[] result = new byte[size];
            int offset = 0;
            for (int i = 0; i < _partsCount; i++)
            {
                offset += ReliableChannel.ReadData(_packets[i], result, offset);
            }

            return result;
        }
    }

    public class ReliableOutgoingPacket : IDisposable
    {
        private const int _maxParts = short.MaxValue / 2;

        private readonly int _maxPacketSize;
        private readonly Packet[] _packets;
        private int _partsCount;
        private short _ack = -1;
        private bool _hasPackets;

        public ReliableOutgoingPacket(int maxPacketSize)
        {
            _maxPacketSize = maxPacketSize - ReliableChannel.ReliableChannelHeaderSize;
            _packets = new Packet[_maxParts];
            _partsCount = 0;
            _ack = -1;
            Seq = 0;
        }

        public bool IsCompleted => _partsCount == _ack + 1;
        public int Seq { get; private set; }
        public bool HasPackets => _hasPackets;

        public void Ack(short ack)
        {
            _ack = ack;
        }

        public void Split(byte seq, byte[] buffer, int offset, int size, EndPoint endPoint, ArrayPool<byte> pool)
        {
            _hasPackets = true;
            _ack = -1;
            Seq = seq;
            int divide = size / _maxPacketSize;
            int modulo = size % _maxPacketSize;
            _partsCount = divide + (modulo > 0 ? 1 : 0);
            if (_partsCount >= _packets.Length)
                throw new OverflowException("Buffer is to big to send!");
            for (int i = 0; i < _partsCount - 1; i++)
                _packets[i] = CreatePacket(i, buffer, offset + (i * _maxPacketSize), _maxPacketSize, seq,
                    PacketType.Reliable, endPoint, pool);
            int lastPacketSize = modulo > 0 ? modulo : _maxPacketSize;
            _packets[_partsCount - 1] = CreatePacket(_partsCount - 1, buffer,
                offset + (_maxPacketSize * (_partsCount - 1)), lastPacketSize, seq, PacketType.ReliablePacketEnd,
                endPoint, pool);
        }

        private Packet CreatePacket(int i, byte[] buffer, int offset, int size, byte seq, PacketType packetType,
            EndPoint endPoint, ArrayPool<byte> pool)
        {
            var packet = new Packet(ReliableChannel.ReliableChannelHeaderSize + size, pool);
            packet.Assign(endPoint);
            ReliableChannel.AddHeader(ref packet, seq, (short)i, packetType);
            ReliableChannel.AddData(ref packet, buffer, offset, size);
            return packet;
        }

        public Span<Packet> GetNotAckedPackets()
        {
            int ack = Math.Max(0, (int)_ack);
            return new Span<Packet>(_packets, ack, _partsCount - ack);
        }

        public void Reset()
        {
            _hasPackets = false;
            for (int i = _ack + 1; i < _partsCount; i++)
                _packets[i].Dispose();
            _partsCount = 0;
            _ack = -1;
        }

        public void Dispose()
        {
            Reset();
        }
    }
}