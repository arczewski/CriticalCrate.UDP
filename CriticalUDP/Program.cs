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

using var socket = new CriticalSocket();
byte[] bytes = Encoding.UTF8.GetBytes(shortMessage);

Console.Write("IP: ");
string ip = Console.ReadLine();
Console.Write("PORT: ");
int port = int.Parse(Console.ReadLine());
Console.Write("[s]erver/[c]lient?");
var input = Console.ReadLine();
if (input == "s")
{
    socket.Listen(new IPEndPoint(IPAddress.Parse(ip), port), 10);
    EndPoint clientEndpoint = null;
    socket.OnConnected += socketId => clientEndpoint = socket.ConnectionManager.GetEndPoint(socketId);
    while (clientEndpoint == null)
    {
        while (socket.Pool(out var packet, out var eventsLeft))
        {
            
        }
    }
    
    Console.WriteLine("Sending!");
    while (true)
    {
        while (socket.Pool(out var packet, out var eventsLeft))
        {
            
        }
        socket.Send(clientEndpoint, bytes, 0, bytes.Length, SendMode.Unreliable);
    }
}
else if (input == "c")
{
    bool connected = false;
    socket.Connect(new IPEndPoint(IPAddress.Parse(ip), port), 1000, (success) =>
    {
        connected = success;
        Console.WriteLine("Connected");
    });
    
    while (socket.Pool(out var packet, out var eventsLeft) || !connected)
    {
        
    }
    Console.WriteLine("Pooling");
    Stopwatch watch = new Stopwatch();
    watch.Start();
    int messages = 0;
    while (watch.ElapsedMilliseconds <= 5000)
    {
        while (socket.Pool(out var packet, out var eventsLeft))
        {
            packet.Dispose();
            messages++;
        }
    }
    Console.WriteLine($"Reliable messages per seconds: {messages / 5.0f}");
}

Console.ReadKey();

