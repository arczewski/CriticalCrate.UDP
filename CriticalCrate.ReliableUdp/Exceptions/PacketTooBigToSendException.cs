namespace CriticalCrate.ReliableUdp.Exceptions;

public class PacketTooBigToSendException(string message) : Exception(message);