using System.Net;
using System.Text;
using CriticalCrate.UDP;

CriticalSocket client = new CriticalSocket(1000 * 1000);
CriticalSocket server = new CriticalSocket(1000 * 1000);

server.Listen(new IPEndPoint(IPAddress.Any, 4444), 10);
var serverPool = new Task(async () =>
{
    while (true)
    {
        while (server.Pool(out var packet, out int eventsLeft))
        {
            //echo back packet
            server.Send(packet.EndPoint, packet.Data, 0, packet.Position,
                SendMode.Reliable);
        }
    }
});
var clientPool = new Task(async () =>
{
    while (true)
    {
        while (client.Pool(out var packet, out int eventsLeft))
        {
            Console.WriteLine(
                $"Server responded with: {Encoding.UTF8.GetString(packet.Data, 0, packet.Position)}");
        }
    }
});

bool isConnected = false;
client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4444), 10000, (connected) => isConnected = connected);
serverPool.Start();
clientPool.Start();

Console.WriteLine("Send message to server for echo response");
while (true)
{
    while (!isConnected) ;
    string? data = Console.ReadLine();
    if (string.IsNullOrEmpty(data))
    {
        Console.WriteLine("Empty string :(");
        continue;
    }

    Console.Clear();
    byte[] dataAsBytes = Encoding.UTF8.GetBytes(data);
    client.Send(dataAsBytes, 0, dataAsBytes.Length, SendMode.Reliable);
}