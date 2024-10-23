using System.Collections;
using System.Net;
using System.Runtime.InteropServices;
using CriticalCrate.ReliableUdp.Exceptions;
using CriticalCrate.ReliableUdp.Extensions;

namespace CriticalCrate.ReliableUdp.Channels;

public interface IReliableChannel : IChannel, IPacketHandler, IConnectionHandler
{
    void PushOutgoingPackets(DateTime now);
    
    event Action<Packet> OnPacketReceived;
    void OnPingUpdated(EndPoint endpoint, long ping);
}

internal class ReliableChannel(ISocket socket, IPacketFactory packetFactory) : IReliableChannel
{
    public const int HeaderSize = FlagSize + VersionSize + PacketIdSize + PacketsCountSize + SeqAckSize;
    public const int AckPosition = HeaderSize - SeqAckSize;
    public const int PacketsCountPosition = HeaderSize - SeqAckSize - PacketsCountSize;
    public event Action<Packet>? OnPacketReceived;

    private const int FlagSize = sizeof(byte);
    private const int VersionSize = sizeof(byte);
    private const int PacketIdSize = sizeof(ushort);
    private const int PacketsCountSize = sizeof(ushort);
    private const int SeqAckSize = sizeof(ushort);

    private readonly Dictionary<EndPoint, OutgoingPacketHandler> _outgoingPacketHandlers = new();
    private readonly Dictionary<EndPoint, IncomingPacketHandler> _incomingPacketHandlers = new();

    public void Send(Packet packet)
    {
        if (!_outgoingPacketHandlers.TryGetValue(packet.EndPoint, out var handler))
            throw new UnrecognizedEndpointException(packet.EndPoint.ToString() ?? string.Empty);
        handler.Enqueue(packet);
        SendOutgoingPackets(handler, handler.GetPacketsToSend(DateTime.Now));
    }
    public void OnPingUpdated(EndPoint endpoint, long ping)
    {
        if (!_outgoingPacketHandlers.TryGetValue(endpoint, out var handler))
            return;
        handler.OnPingUpdated(ping);
    }

    private void SendOutgoingPackets(OutgoingPacketHandler handler, ReadOnlySpan<Packet> packets)
    {
        if (packets.Length == 0) return;
        foreach(var packet in packets)
            socket.Send(packetFactory.CreatePacket(packet));
        handler.MarkAsSent(DateTime.Now);
    }

    public void PushOutgoingPackets(DateTime now)
    {
        foreach (var outgoingPacketHandler in _outgoingPacketHandlers.Values)
            SendOutgoingPackets(outgoingPacketHandler, outgoingPacketHandler.GetPacketsToSend(now));
    }

    public void HandlePacket(in Packet receivedPacket, in PacketType packetType, in ushort packetId)
    {
        if (!packetType.HasFlag(PacketType.Reliable)) return;
        if (packetType.HasFlag(PacketType.Reliable | PacketType.Ack))
        {
            if (!_outgoingPacketHandlers.TryGetValue(receivedPacket.EndPoint, out var outgoingPacketBuilder))
                return;
            if (!outgoingPacketBuilder.HasPackets || outgoingPacketBuilder.ReliablePacketId != packetId)
                return;
            var incomingAck = ReadAck(receivedPacket);
            outgoingPacketBuilder.SliceDelivered(incomingAck);
            return;
        }

        if (!_incomingPacketHandlers.TryGetValue(receivedPacket.EndPoint, out var incomingPacketBuilder))
            return;

        var isOldPacket = packetId < incomingPacketBuilder.PacketId ||
                          Math.Abs(packetId - incomingPacketBuilder.PacketId) > ushort.MaxValue / 2;
        if (isOldPacket)
        {
            var oldAck = ReadAck(receivedPacket);
            var ackPacket = packetFactory.CreateReliableAck(receivedPacket, oldAck);
            socket.Send(ackPacket);
            return;
        }

        var seq = ReadAck(receivedPacket);
        if (incomingPacketBuilder.ReceiveSlice(packetId, seq, receivedPacket))
        {
            var ackPacket =
                packetFactory.CreateReliableAck(receivedPacket, incomingPacketBuilder.LastAcknowledgedSlice);
            socket.Send(ackPacket);
        }

        if (!incomingPacketBuilder.IsComplete()) return;
        var reliablePacket = incomingPacketBuilder.Build();
        try
        {
            OnPacketReceived?.Invoke(reliablePacket);
        }
        finally
        {
            incomingPacketBuilder.Next();
        }
    }

    private ushort ReadAck(in Packet receivedPacket)
    {
        return BitConverter.ToUInt16(receivedPacket.Buffer[AckPosition..]);
    }

    public void Dispose()
    {
        foreach (var keyValue in _incomingPacketHandlers)
            keyValue.Value.Dispose();
        foreach (var keyValue in _outgoingPacketHandlers)
            keyValue.Value.Dispose();
    }

    public void HandleConnection(EndPoint endPoint)
    {
        _outgoingPacketHandlers.Add(endPoint, new OutgoingPacketHandler(packetFactory));
        _incomingPacketHandlers.Add(endPoint, new IncomingPacketHandler(packetFactory));
    }

    public void HandleDisconnection(EndPoint endPoint)
    {
        if (_outgoingPacketHandlers.Remove(endPoint, out var outgoingPacketBuilder))
            outgoingPacketBuilder.Dispose();

        if (_incomingPacketHandlers.Remove(endPoint, out var incomingPacketBuilder))
            incomingPacketBuilder.Dispose();
    }
}

internal sealed class IncomingPacketHandler(IPacketFactory packetFactory) : IDisposable
{
    private const int MaxPacketsCount = ushort.MaxValue - 1;
    public ushort SliceCount { get; private set; }
    public ushort LastAcknowledgedSlice { get; private set; }
    public ushort PacketId { get; private set; } = 1;

    private readonly BitArray _ackBuffer = new(MaxPacketsCount);
    private int _byteSize;
    private Packet _reconstructedPacket;

    public void Next()
    {
        PacketId++;
        SliceCount = 0;
        LastAcknowledgedSlice = 0;
        _byteSize = 0;
        packetFactory.ReturnPacket(_reconstructedPacket);
        _ackBuffer.SetAll(false);
    }

    public bool ReceiveSlice(ushort packetId, ushort seq, Packet packet)
    {
        var buffer = packet.Buffer;
        if (SliceCount == 0)
        {
            var slices = BitConverter.ToUInt16(buffer[ReliableChannel.PacketsCountPosition..]);
            SliceCount = slices;
            _reconstructedPacket = packetFactory.CreatePacket(packet.EndPoint,
                SliceCount * (ISocket.Mtu - ReliableChannel.HeaderSize));
        }

        if (packetId != PacketId)
            return false;

        if (seq < LastAcknowledgedSlice)
            return false;

        _ackBuffer[seq - 1] = true;
        packet.Buffer[ReliableChannel.HeaderSize..]
            .CopyTo(
                _reconstructedPacket.Buffer[
                    ((seq - 1) * (ISocket.Mtu - ReliableChannel.HeaderSize))..]); // TODO cache calculation

        if (seq == SliceCount)
            _byteSize = ((SliceCount - 1) * (ISocket.Mtu - ReliableChannel.HeaderSize)) + (packet.Position - ReliableChannel.HeaderSize);
        
        if (LastAcknowledgedSlice + 1 != seq) return false;
        LastAcknowledgedSlice = seq;
        for (ushort i = (ushort)(seq + 1); i < SliceCount; i++)
        {
            if (!_ackBuffer[i - 1])
                break;
            LastAcknowledgedSlice = i;
        }

        return true;
    }

    public void Dispose()
    {
        if (SliceCount != 0)
            packetFactory.ReturnPacket(_reconstructedPacket);
    }

    public bool IsComplete()
    {
        return LastAcknowledgedSlice == SliceCount;
    }

    public Packet Build()
    {
        _reconstructedPacket = _reconstructedPacket with { Position = _byteSize };
        return _reconstructedPacket;
    }
}

internal sealed class OutgoingPacketHandler(IPacketFactory packetFactory) : IDisposable
{
    private const int MaxPacketSize = ISocket.Mtu - ReliableChannel.HeaderSize;
    private readonly TimeSpan _initialWaitTimeForAck = TimeSpan.FromMilliseconds(100);
    public ushort ReliablePacketId { get; private set; }
    public bool IsCompleted => _outgoingSlices.Count == _acknowledgedSlices;
    public bool HasPackets => _outgoingSlices.Count > 0;
    private const int MaxPacketsCount = ushort.MaxValue - 1;
    private readonly List<Packet> _outgoingSlices = new(MaxPacketsCount);
    private readonly Queue<Packet> _pendingOutgoingPackets = new();
    private ushort _acknowledgedSlices;
    private DateTime _lastSendTime = DateTime.MinValue;
    private TimeSpan _waitTimeForAck;
    
    public void SliceDelivered(ushort sliceSequenceNumber)
    {
        if(_acknowledgedSlices < sliceSequenceNumber)
            _acknowledgedSlices = sliceSequenceNumber;
        if (IsCompleted && _outgoingSlices.Count > 0)
        {
            foreach (var packet in _outgoingSlices)
                packetFactory.ReturnPacket(packet);
            _outgoingSlices.Clear();
            if(_pendingOutgoingPackets.TryDequeue(out var outgoingPacket))
                Next(outgoingPacket);
        }
    }

    public void OnPingUpdated(long ping)
    {
        _waitTimeForAck = TimeSpan.FromMilliseconds(ping + 1);
    }

    private void Next(Packet packet)
    {
        _waitTimeForAck = _initialWaitTimeForAck;
        _lastSendTime = DateTime.MinValue;
        _outgoingSlices.Clear();
        _acknowledgedSlices = 0;
        ReliablePacketId += 1;
        int size = packet.Buffer.Length;
        var divide = size / MaxPacketSize;
        var modulo = size % MaxPacketSize;
        var requiredPacketsCount = divide + (modulo > 0 ? 1 : 0);
        if (requiredPacketsCount >= MaxPacketsCount)
            throw new PacketTooBigToSendException("Packet is too big to send");
        FillPacketSlicesIntoList(_outgoingSlices, (ushort)requiredPacketsCount, modulo == 0 ? MaxPacketSize : modulo, packet);
        packetFactory.ReturnPacket(packet);
    }

    private void FillPacketSlicesIntoList(List<Packet> packets, ushort requiredPacketsCount, int lastSliceSize, Packet packetToSlice)
    {
        packets.Clear();
        var buffer = packetToSlice.Buffer;
        for (var i = 0; i < requiredPacketsCount; i++)
        {
            int packetSize = i == requiredPacketsCount - 1 ? lastSliceSize + ReliableChannel.HeaderSize : ISocket.Mtu;
            var packetSlice = packetFactory.CreatePacket(packetToSlice.EndPoint, packetSize);
            var packetSliceBuffer = packetSlice.Buffer;
            packetSliceBuffer[0] = (byte)PacketType.Reliable;
            packetSliceBuffer[1] = 1; // version
            BitConverter.TryWriteBytes(packetSliceBuffer[2..], ReliablePacketId);
            BitConverter.TryWriteBytes(packetSliceBuffer[4..], requiredPacketsCount); // TODO use reliable channel const
            BitConverter.TryWriteBytes(packetSliceBuffer[6..], (ushort)(i + 1));
            buffer.Slice(i * MaxPacketSize, i == requiredPacketsCount - 1 ? lastSliceSize : MaxPacketSize).CopyTo(packetSliceBuffer[ReliableChannel.HeaderSize..]);
            packets.Add(packetSlice);
        }
    }

    public void Dispose()
    {
        foreach(var packet in _pendingOutgoingPackets) 
            packetFactory.ReturnPacket(packet);
        _pendingOutgoingPackets.Clear();
    }

    public void Enqueue(Packet packet)
    {
        _pendingOutgoingPackets.Enqueue(packet);
        if(!HasPackets)
            Next(_pendingOutgoingPackets.Dequeue());
    }

    public ReadOnlySpan<Packet> GetPacketsToSend(DateTime now)
    {
        if (_outgoingSlices.Count > 0 && _lastSendTime.Add(_waitTimeForAck) <= now)
        {
            return CollectionsMarshal.AsSpan(_outgoingSlices)[_acknowledgedSlices..];
        }

        return Span<Packet>.Empty;
    }


    public void MarkAsSent(DateTime now)
    {
        _lastSendTime = now;
        _waitTimeForAck = _waitTimeForAck.Add(_initialWaitTimeForAck);
    }
}