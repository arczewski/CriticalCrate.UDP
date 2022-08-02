using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CriticalCrate.UDP;

string loremIpsum =
    "Lorem ipsum dolor sit amet, consectetuer adipiscing elit. Aenean commodo ligula eget dolor. Aenean massa. Cum sociis natoque penatibus et magnis dis parturient montes, nascetur ridiculus mus. Donec quam felis, ultricies nec, pellentesque eu, pretium quis, sem. Nulla consequat massa quis enim. Donec pede justo, fringilla vel, aliquet nec, vulputate eget, arcu. In enim justo, rhoncus ut, imperdiet a, venenatis vitae, justo. Nullam dictum felis eu pede mollis pretium. Integer tincidunt. Cras dapibus. Vivamus elementum semper nisi. Aenean vulputate eleifend tellus. Aenean leo ligula, porttitor eu, consequat vitae, eleifend ac, enim. Aliquam lorem ante, dapibus in, viverra quis, feugiat a, tellus. Phasellus viverra nulla ut metus varius laoreet. Quisque rutrum. Aenean imperdiet. Etiam ultricies nisi vel augue. Curabitur ullamcorper ultricies nisi. Nam eget dui. Etiam rhoncus. Maecenas tempus, tellus eget condimentum rhoncus, sem quam semper libero, sit amet adipiscing sem neque sed ipsum. Nam quam nunc, blandit ve";
string shortMessage = "asddsa";
string longMessage = loremIpsum;

for (int i = 0; i < 12; i++)
    longMessage += loremIpsum;

using var server = new CriticalSocket(100000);
using var client = new CriticalSocket(100000);
int clientSocketId = 0;
server.OnConnected += (socketId) =>
{
    clientSocketId = socketId;
    Console.WriteLine($"Client connected - {socketId}");
};
server.OnDisconnected += (socketId) => Console.WriteLine($"Client disconnected - {socketId}");
client.OnDisconnected += (socketId) => Console.WriteLine("Clientside disconnected");

var serverEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5000);
server.Listen(serverEndpoint);
bool isConnected = false;
client.Connect(serverEndpoint, 1000, (result) =>
{
    Console.WriteLine($"Connection status: {result}");
    isConnected = true;
});

byte[] bytes = Encoding.UTF8.GetBytes(longMessage);
while (!isConnected)
{
    while (server.Pool(out var packet, out var eventsLeft))
    {
        packet.Dispose();
    }

    while (client.Pool(out var packet2, out var eventsLeft2))
    {
        packet2.Dispose();
    }

    Thread.Sleep(15);
}

EndPoint clientEndpoint = server.ConnectionManager.GetEndPoint(clientSocketId);
client.Send(bytes, 0, bytes.Length, SendMode.Reliable);
server.Send(clientEndpoint, bytes, 0, bytes.Length, SendMode.Reliable);
Stopwatch watch = new Stopwatch();
watch.Start();
int messages = 0;
while (watch.ElapsedMilliseconds < 5000)
{
    while (server.Pool(out var packet, out var eventsLeft))
    {
        Debug.Assert(packet.Position == bytes.Length);
        packet.Dispose();
        client.Send(bytes, 0, bytes.Length, SendMode.Reliable);
        messages++;
    }

    while (client.Pool(out var packet2, out var eventsLeft2))
    {
        Debug.Assert(packet2.Position == bytes.Length);
        packet2.Dispose();
        server.Send(clientEndpoint, bytes, 0, bytes.Length, SendMode.Reliable);
        messages++;
    }
}

Console.WriteLine($"Reliable messages per seconds: {messages / 5.0f}");