using Library;
using System.Net;
class Program
{
    private static Client _client;

    static void Main(string[] args)
    {
        _client = new Client();
        _client.ConnectedServer += _client_ConnectedServer;
        _client.DisconnectedServer += _client_DisconnectedServer;
        _client.ReceivedMessageFromServer += _client_ReceivedMessageFromServer;

        _client.ServerIPAddress = IPAddress.Parse("127.0.0.1");
        _client.ServerPort = 9000;
        _client.ConnectServer();
        while (true)
        {
            string message = Console.ReadLine();

            if (message != null)
            {
                _client.SendMessageToServer(message);
               
            }
        }

    }
    private static void _client_ReceivedMessageFromServer(string message)
    {
        Console.WriteLine(message);
    }

    private static void _client_DisconnectedServer(string message)
    {
        Console.WriteLine(message);

    }

    private static void _client_ConnectedServer(LocalInfo info)
    {
        Console.WriteLine(info.PCName + " - " + info.IPAddress + ":" + info.LocalEndPoint + " Message: Connected to Server");
        _client.SendMessageToServer("Hello");
    }
    
}
