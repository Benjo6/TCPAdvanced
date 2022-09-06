using Library;
using Library.Extension_Methods;
using Library.MessageModel;
using Library.Tcp;
using Newtonsoft.Json;
using System.Net;
using System.Text;

#region Delegates Methods
public delegate void ServerConnectedEventHandler(LocalInfo info);
public delegate void ServerDisconnectedEventHandler(string message);
public delegate void ReceivedMessageFromServerEventHandler(string message);
#endregion

public class Client
{

    #region Variables
    public event ServerConnectedEventHandler ConnectedServer;
    public event ServerDisconnectedEventHandler DisconnectedServer;
    public event ReceivedMessageFromServerEventHandler ReceivedMessageFromServer;

    private Tcp_Client _client;
    private Thread _threadInternetControlListener;
    private Thread _threadListen;
    private bool _isServerConnected;
    private bool _flagOfThreadsBreak = true;
    private string _key;
    #endregion
    #region Constructors
    public Client()
    {
        _key = null;
        _threadListen = new Thread(ServerListen);
        _threadListen.IsBackground = true;
        _threadListen.Start();

        _threadInternetControlListener = new Thread(InternetListen);
        _threadInternetControlListener.IsBackground = true;
        _threadInternetControlListener.Start();
    }
    public Client(string key)
    {
        _key = key;

        _threadListen = new Thread(ServerListen);
        _threadListen.IsBackground = true;
        _threadListen.Start();

        _threadInternetControlListener = new Thread(InternetListen);
        _threadInternetControlListener.IsBackground = true;
        _threadInternetControlListener.Start();

    }

    #endregion
    #region Properties
    public IPAddress ServerIPAddress { get; set; }
    public int ServerPort { get; set; }
    #endregion

    #region Methods
    #region Public Methods
    public void ConnectServer()
    {
        if (_client != null)
        {
            throw new Exception("The server has already been started");
        }

        StartClient();
        _flagOfThreadsBreak = false;
    }
    public void DisconnectServer()
    {
        _flagOfThreadsBreak = true;
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
            _isServerConnected = false;
            DisconnectedServer?.Invoke("No server connection.");
        }

    }
    public bool SendMessageToServer(string message)
    {
        try
        {
            MessageModel model = new MessageModel();
            model.Type = Library.MessageModel.Type.Message;
            model.Message = message;
            model.PCName = Environment.MachineName;
            string json = JsonConvert.SerializeObject(model);

            if (_key == null)
                return _client.Send(Encoding.UTF8.GetBytes(json));
            
            return _client.Send(EncryptExtensions.Encrypt(json, _key));
        }
        catch
        {
            return false;
        }
    }
    #endregion

    #region Private Methods

    private void StartClient()
    {
        try
        {
            _client = new Tcp_Client(ServerIPAddress.ToString(), ServerPort, ServerConnected, ServerDisconnected, MessageReceived, false);
            _isServerConnected = true;

            MessageModel message = new MessageModel();
            message.Type = Library.MessageModel.Type.ClientLogon;
            message.Message = String.Empty;
            message.PCName = Environment.MachineName;

            string json = JsonConvert.SerializeObject(message);

            if (_key == null)
                _client.Send(Encoding.UTF8.GetBytes(json));
            else
                _client.Send(EncryptExtensions.Encrypt(json, _key));
        }
        catch
        {
            _isServerConnected=false;
            DisconnectedServer?.Invoke("No server connection");
        }
    }

    private void ServerListen()
    {
        while (true)
        {
            if (!_flagOfThreadsBreak)
            {
                if (!_isServerConnected)
                    StartClient();
            }

            Thread.Sleep(1000);
        }
    }

    private void InternetListen()
    {
        while (true)
        {
            if (!_flagOfThreadsBreak)
            {
                try
                {
                    bool status = _client != null && _client.Send(new byte[] { });
                    if (!status)
                    {
                        if (_client != null)
                        {
                            _client.Dispose();
                            _client = null;
                        }
                        _isServerConnected = false;

                    }
                }
                catch
                {
                    if (_client != null)
                    {
                        _client.Dispose();
                        _client = null;
                    }
                    _isServerConnected = false;
                }
                Thread.Sleep(1000);
            }
        }

    }
    private bool MessageReceived(byte[] data)
    {
        string receiveData = string.Empty;

        if (_key == null)
            receiveData = Encoding.UTF8.GetString(data);
        else
            receiveData = Encoding.UTF8.GetString(DecryptExtensions.Decrypt(Convert.ToBase64String(data), _key));
        

        var message = JsonConvert.DeserializeObject<MessageModel>(receiveData);


        if (message == null) 
            return true;

        LocalInfo info;

        info.IPAddress = _client.LocalAddress;
        info.LocalEndPoint = Convert.ToInt32(_client.LocalPort);
        info.PCName = Environment.MachineName;

        if (message.Type == Library.MessageModel.Type.AcceptLogon)
        {
            _isServerConnected |= true;
            ConnectedServer?.Invoke(info);
        }

        if(message.Type == Library.MessageModel.Type.Disconnect)
        {
            _client.Dispose();
            _client = null;
            _flagOfThreadsBreak = true;
            _isServerConnected = false;
            DisconnectedServer?.Invoke("Connection disconnected by Server");
        }

        if (message.Type == Library.MessageModel.Type.Blocked)
        {
            _client.Dispose();
            _client = null;
            _isServerConnected = false;
            DisconnectedServer?.Invoke("It does not connect to the server beacuse it is a blocked IP");
        }

        if (message.Type == Library.MessageModel.Type.Message)
        {
            ReceivedMessageFromServer?.Invoke(message.Message);
        }
        return true;

    }

    private bool ServerConnected()
    {
        return true;
    }
    private bool ServerDisconnected()
    {
        _client.Dispose();
        _client = null;
        _isServerConnected = false;
        return true;
    }

    #endregion

    #endregion

}