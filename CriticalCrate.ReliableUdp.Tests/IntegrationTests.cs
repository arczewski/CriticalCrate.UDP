using System.Net;
using System.Text;
using CriticalCrate.ReliableUdp.Extensions;
using FluentAssertions;

namespace CriticalCrate.ReliableUdp.Tests;

public class IntegrationTests
{
    [Fact]
    public void Can_Connect()
    {
        // Arrange
        using var client = CriticalSocketExtensions.CreateClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        using var server = CriticalSocketExtensions.CreateServer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 1);

        // Act
        server.Listen(new IPEndPoint(IPAddress.Any, 4444));
        client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4444));
        while (server.ConnectionManager.ConnectedClients.Count != 1 || !client.ConnectionManager.Connected)
        {
            server.Pool();
            client.Pool();
        }

        // Assert
        client.ConnectionManager.Connected.Should().BeTrue();
        server.ConnectionManager.ConnectedClients.Should().HaveCount(1);
    }

    [Fact]
    public void Can_Send_And_Receive_Unreliable()
    {
        // Arrange
        using var client = CriticalSocketExtensions.CreateClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        using var server = CriticalSocketExtensions.CreateServer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 1);
        var packetFactory = new PacketFactory();
        server.Listen(new IPEndPoint(IPAddress.Any, 4444));
        client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4444));
        while (server.ConnectionManager.ConnectedClients.Count != 1 || !client.ConnectionManager.Connected)
        {
            server.Pool();
            client.Pool();
        }

        var messageFromClient = "Hello World from client"u8.ToArray();
        var messageFromServer = "Hello World from server"u8.ToArray();
        var messagesToSend = 1000000;
        
        var receivedOnClient = 0;
        var receivedOnServer = 0;
        client.OnPacketReceived += packet =>
        {
            packet.Buffer.ToArray().Should().BeEquivalentTo(messageFromServer);
            receivedOnClient++;
        };
        server.OnPacketReceived += packet =>
        {
            packet.Buffer.ToArray().Should().BeEquivalentTo(messageFromClient);
            receivedOnServer++;
        };
        
        // Act
        for (var i = 0; i < messagesToSend; i++)
        {
            var clientPacket = packetFactory
                .CreatePacket(client.ServerEndpoint, messageFromClient, 0, messageFromClient.Length);
            var serverPacket = packetFactory
                .CreatePacket(server.ConnectionManager.ConnectedClients.Single(), messageFromServer, 0,
                    messageFromServer.Length);
            client.Send(clientPacket, SendMode.Unreliable);
            server.Send(serverPacket, SendMode.Unreliable);
            client.Pool();
            server.Pool();
        }

        // Assert

        while (receivedOnClient < messagesToSend || receivedOnServer < messagesToSend)
        {
            client.Pool();
            server.Pool();
        }

        receivedOnClient.Should().Be(messagesToSend);
        receivedOnServer.Should().Be(messagesToSend);
    }

    [Fact]
    public void Can_Send_And_Receive_Reliable_Messages()
    {
        // Arrange
        using var client = CriticalSocketExtensions.CreateClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        using var server = CriticalSocketExtensions.CreateServer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 1);
        var packetFactory = new PacketFactory();
        server.Listen(new IPEndPoint(IPAddress.Any, 6666));
        client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 6666));
        while (server.ConnectionManager.ConnectedClients.Count != 1 || !client.ConnectionManager.Connected)
        {
            server.Pool();
            client.Pool();
        }

        var messageFromClient = "Hello World from client"u8.ToArray();
        var messageFromServer = "Hello World from server"u8.ToArray();
        var messagesToSend = 100000;
        
        // Act
        for (var i = 0; i < messagesToSend; i++)
        {
            var clientPacket = packetFactory
                .CreatePacket(client.ServerEndpoint, messageFromClient, 0, messageFromClient.Length);
            var serverPacket = packetFactory
                .CreatePacket(server.ConnectionManager.ConnectedClients.Single(), messageFromServer, 0,
                    messageFromServer.Length);
            client.Send(clientPacket, SendMode.Reliable);
            server.Send(serverPacket, SendMode.Reliable);
        }

        // Assert
        var receivedOnClient = 0;
        var receivedOnServer = 0;
        client.OnPacketReceived += packet =>
        {
            packet.Buffer.ToArray().Should().BeEquivalentTo(messageFromServer);
            receivedOnClient++;
        };
        server.OnPacketReceived += packet =>
        {
            packet.Buffer.ToArray().Should().BeEquivalentTo(messageFromClient);
            receivedOnServer++;
        };

        while (receivedOnClient < messagesToSend || receivedOnServer < messagesToSend)
        {
            client.Pool();
            server.Pool();
        }

        receivedOnClient.Should().Be(messagesToSend);
        receivedOnServer.Should().Be(messagesToSend);
    }
    
    [Fact]
    public void Can_Send_And_Receive_Long_Reliable_Messages()
    {
        // Arrange
        using var client = CriticalSocketExtensions.CreateClient(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        using var server = CriticalSocketExtensions.CreateServer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 1);
        var packetFactory = new PacketFactory();
        server.Listen(new IPEndPoint(IPAddress.Any, 5555));
        client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5555));
        while (server.ConnectionManager.ConnectedClients.Count != 1 || !client.ConnectionManager.Connected)
        {
            server.Pool();
            client.Pool();
        }

        var messageFromClient = Encoding.UTF8.GetBytes(string.Join("", Enumerable.Repeat("Hello World from client", 10000)));
        var messageFromServer = Encoding.UTF8.GetBytes(string.Join("", Enumerable.Repeat("Hello World from server", 10000)));
        var messagesToSend = 100;
        
        // Act
        for (var i = 0; i < messagesToSend; i++)
        {
            var clientPacket = packetFactory
                .CreatePacket(client.ServerEndpoint, messageFromClient, 0, messageFromClient.Length);
            var serverPacket = packetFactory
                .CreatePacket(server.ConnectionManager.ConnectedClients.Single(), messageFromServer, 0,
                    messageFromServer.Length);
            client.Send(clientPacket, SendMode.Reliable);
            server.Send(serverPacket, SendMode.Reliable);
        }

        // Assert
        var receivedOnClient = 0;
        var receivedOnServer = 0;
        client.OnPacketReceived += packet =>
        {
            packet.Buffer.ToArray().Should().BeEquivalentTo(messageFromServer);
            receivedOnClient++;
        };
        server.OnPacketReceived += packet =>
        {
            packet.Buffer.ToArray().Should().BeEquivalentTo(messageFromClient);
            receivedOnServer++;
        };

        while (receivedOnClient < messagesToSend || receivedOnServer < messagesToSend)
        {
            client.Pool();
            server.Pool();
        }

        receivedOnClient.Should().Be(messagesToSend);
        receivedOnServer.Should().Be(messagesToSend);
    }
}