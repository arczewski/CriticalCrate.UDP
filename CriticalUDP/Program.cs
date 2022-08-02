using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CriticalCrate.UDP;

const string message =
    "Lorem ipsum dolor sit amet, consectetuer adipiscing elit. Aenean commodo ligula eget dolor. Aenean massa. Cum sociis natoque penatibus et magnis dis parturient montes, nascetur ridiculus mus. Donec quam felis, ultricies nec, pellentesque eu, pretium quis, sem. Nulla consequat massa quis enim. Donec pede justo, fringilla vel, aliquet nec, vulputate eget, arcu. In enim justo, rhoncus ut, imperdiet a, venenatis vitae, justo. Nullam dictum felis eu pede mollis pretium. Integer tincidunt. Cras dapibus. Vivamus elementum semper nisi. Aenean vulputate eleifend tellus. Aenean leo ligula, porttitor eu, consequat vitae, eleifend ac, enim. Aliquam lorem ante, dapibus in, viverra quis, feugiat a, tellus. Phasellus viverra nulla ut metus varius laoreet. Quisque rutrum. Aenean imperdiet. Etiam ultricies nisi vel augue. Curabitur ullamcorper ultricies nisi. Nam eget dui. Etiam rhoncus. Maecenas tempus, tellus eget condimentum rhoncus, sem quam semper libero, sit amet adipiscing sem neque sed ipsum. Nam quam nunc, blandit ve";
const string shortMessage = "asddsa";
using var server = new CriticalSocket();
using var client = new CriticalSocket();
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

byte[] bytes = Encoding.UTF8.GetBytes(shortMessage);
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
client.Send(bytes, 0, bytes.Length, SendMode.Unreliable);
server.Send(clientEndpoint, bytes, 0, bytes.Length, SendMode.Unreliable);
Stopwatch watch = new Stopwatch();
watch.Start();
int messages = 0;
while (watch.ElapsedMilliseconds < 5000)
{
    while (server.Pool(out var packet, out var eventsLeft))
    {
        packet.Dispose();
        client.Send(bytes, 0, bytes.Length, SendMode.Unreliable);
        messages++;
    }

    while (client.Pool(out var packet2, out var eventsLeft2))
    {
        packet2.Dispose();
        server.Send(clientEndpoint, bytes, 0, bytes.Length, SendMode.Unreliable);
        messages++;
    }
}

Console.WriteLine($"Reliable messages per seconds: {messages / 5.0f}");