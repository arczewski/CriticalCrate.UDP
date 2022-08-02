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

server.OnConnected += (socketId) => Console.WriteLine($"Client connected - {socketId}");
server.OnDisconnected += (socketId) => Console.WriteLine($"Client disconnected - {socketId}");
client.OnDisconnected += (socketId) => Console.WriteLine("Clientside disconnected");

server.Listen(new IPEndPoint(IPAddress.Parse("10.0.2.15"),5000));
bool isConnected = false;
client.Connect(new IPEndPoint(IPAddress.Parse("10.0.2.15"), 5000), 1000, (result) =>
{
    Console.WriteLine($"Connection status: {result}");
    isConnected = true;
});

byte[] bytes = Encoding.UTF8.GetBytes(shortMessage);
while (!isConnected)
{
    server.Pool(out var packet, out var eventsLeft);
    client.Pool(out var packet2, out var eventsLeft2);
    Thread.Sleep(12);
}
client.Send(bytes, 0, bytes.Length, SendMode.Reliable);
while (true)
{
    if(server.Pool(out var packet, out var eventsLeft))
        Console.WriteLine($"Received: {Encoding.UTF8.GetString(packet.Data, 0, packet.Data.Length)}");
    client.Pool(out var packet2, out var eventsLeft2);
    client.Send(bytes, 0, bytes.Length, SendMode.Reliable);
    Thread.Sleep(12);
}

