using Library.Info;
using System.Net;

class Program
{
    private static Server.Server _server;
    static void Main(string[] args)
    {

        
        try
        {
            _server = new Server.Server();

            _server.ConnectedClient += Server_ConnectedClient;
            _server.DisconnectedClient += Server_DisconnectedClient;

            _server.IPAddress = IPAddress.Parse("127.0.0.1");
            _server.Port = 9000;

            //The following computers will be thrown directly from the server when connected.
            _server.BlockedIPList = new List<IPAddress> { IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.1.6") };
            bool isOpened = _server.StartServer();

            if (isOpened) Console.WriteLine(_server.IPAddress + ":" + _server.Port + " - Server started...");
            else Console.WriteLine("Failed to start server!");
            
            _server.ReceivedMessageFromClient += Server_ReceivedMessageFromClient;

            if (Server_ReceivedMessageFromClient != null)
            {

                Console.ReadLine();
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    private static void Server_DisconnectedClient(ClientInfo clientInfo)
    {
        Console.Clear();
        foreach (var client in _server.GetClientList())
        {
            Console.WriteLine(client.PCName + " - " + client.IPAndRemoteEndPoint);
        }
    }

    private static void Server_ConnectedClient(ClientInfo clientInfo)
    {
        Console.Clear();
        foreach (var client in _server.GetClientList())
        {
            Console.WriteLine(client.PCName + " - " + client.IPAndRemoteEndPoint);
        }

        _server.SendMessage(clientInfo, "Welcome to Server");
    }

    private static void Server_ReceivedMessageFromClient(ClientInfo clientInfo, string message)
    {
        Console.WriteLine(clientInfo.PCName + " - " + clientInfo.IPAndRemoteEndPoint + " -> " + message);
    }
}
