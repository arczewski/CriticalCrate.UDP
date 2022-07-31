using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CriticalCrate.UDP;

const string message =
    "Lorem ipsum dolor sit amet, consectetuer adipiscing elit. Aenean commodo ligula eget dolor. Aenean massa. Cum sociis natoque penatibus et magnis dis parturient montes, nascetur ridiculus mus. Donec quam felis, ultricies nec, pellentesque eu, pretium quis, sem. Nulla consequat massa quis enim. Donec pede justo, fringilla vel, aliquet nec, vulputate eget, arcu. In enim justo, rhoncus ut, imperdiet a, venenatis vitae, justo. Nullam dictum felis eu pede mollis pretium. Integer tincidunt. Cras dapibus. Vivamus elementum semper nisi. Aenean vulputate eleifend tellus. Aenean leo ligula, porttitor eu, consequat vitae, eleifend ac, enim. Aliquam lorem ante, dapibus in, viverra quis, feugiat a, tellus. Phasellus viverra nulla ut metus varius laoreet. Quisque rutrum. Aenean imperdiet. Etiam ultricies nisi vel augue. Curabitur ullamcorper ultricies nisi. Nam eget dui. Etiam rhoncus. Maecenas tempus, tellus eget condimentum rhoncus, sem quam semper libero, sit amet adipiscing sem neque sed ipsum. Nam quam nunc, blandit ve";

using UDPSocket server = new UDPSocket(512);
using UDPSocket client = new UDPSocket(512);

server.Listen(5000);
client.Client();

var clientReliableChannel = new ReliableChannel(client);
var serverReliableChannel = new ReliableChannel(server);

server.OnPacketReceived += (packet) =>
{
    serverReliableChannel.OnReceive(packet);
};

int messageCount = 0;
var bytes = System.Text.Encoding.UTF8.GetBytes(message);

client.OnPacketReceived += (packet) =>
{
    messageCount++;
    clientReliableChannel.OnReceive(packet);
    clientReliableChannel.Send(new IPEndPoint(IPAddress.Loopback, 5000), bytes, 0, bytes.Length);
};

serverReliableChannel.OnPacketReceived += (reliablePacket) =>
{
    messageCount++;
    serverReliableChannel.Send(new IPEndPoint(IPAddress.Loopback, 5000), bytes, 0, bytes.Length);
};

Stopwatch watch = new Stopwatch();
watch.Start();
clientReliableChannel.Send(new IPEndPoint(IPAddress.Loopback, 5000), bytes, 0, bytes.Length);
serverReliableChannel.Send(new IPEndPoint(IPAddress.Loopback, 5000), bytes, 0, bytes.Length);
while (watch.ElapsedMilliseconds < 1000)
{
    clientReliableChannel.Update();
    serverReliableChannel.Update();
}
Console.WriteLine(messageCount);