using Library.Extension_Methods;
using Library.Info;
using Library.MessageModel;
using Library.Tcp;
using Newtonsoft.Json;
using System.Net;
using System.Text;

namespace Server
{
    #region Delegate Methods
    public delegate void ClientConnectedEventHandler(ClientInfo info);
    public delegate void ClientDisconnectedEventHandler(ClientInfo info);
    public delegate void ReceivedMessageFromClientEventHandler(ClientInfo info, string message);
    #endregion
    public class Server
    {
        #region Variables
        private Tcp_Server _server;
        private List<ClientInfo> _clientList = new List<ClientInfo>();
        private Thread _threadListen;
        private readonly object _LockOfClientList = new object();
        private bool _flagOfClientListenBreak = true;
        private string _key;
        #endregion

        #region Properties

        public event ClientConnectedEventHandler ConnectedClient;
        public event ClientDisconnectedEventHandler DisconnectedClient;
        public event ReceivedMessageFromClientEventHandler ReceivedMessageFromClient;
        public IPAddress IPAddress { get; set; }
        public int Port { get; set; }
        public List<IPAddress> BlockedIPList { get; set; }
        #endregion

        #region Constructors
        public Server(string key)
        {
            _key = key;
            BlockedIPList = new List<IPAddress>();
            _threadListen = new Thread(ClientListen);
            _threadListen.IsBackground = true;
            _threadListen.Start();
        }
        public Server()
        {
            _key = null;
            BlockedIPList = new List<IPAddress>();
            _threadListen = new Thread(ClientListen);
            _threadListen.IsBackground = true;
            _threadListen.Start();
        }

        #endregion
        #region Public Methods

        public bool StartServer()
        {
            if (_server != null)
                throw new Exception("The server has already been started");

            try
            {
                _server = new Tcp_Server(IPAddress.ToString(), Port, ClientConnected, ClientDisconnected, MessageReceived, false);
                _flagOfClientListenBreak = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void StopServer()
        {
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
                _flagOfClientListenBreak = true;
            }
        }

        public bool SendMessage(ClientInfo
             info, string message)
        {
            var resultBlocked = (from x in BlockedIPList where x == info.IPAddress select x).FirstOrDefault();

            if (resultBlocked != null)
                return false;

            MessageModel model = new MessageModel();
            model.Type = Library.MessageModel.Type.Message;
            model.Message = message;
            string json = JsonConvert.SerializeObject(model);

            if (_key == null)
                return _server.Send(info.IPAndRemoteEndPoint, Encoding.UTF8.GetBytes(json));

            return _server.Send(info.IPAndRemoteEndPoint, EncryptExtensions.Encrypt(json, _key));
        }

        public List<ClientInfo> GetClientList()
        {
            lock (_LockOfClientList)
                return _clientList;
        }

        public void DisconnectClient(ClientInfo info)
        {
            MessageModel disconnectClientMessage = new MessageModel();
            disconnectClientMessage.Type = Library.MessageModel.Type.Disconnect;
            string json = JsonConvert.SerializeObject(disconnectClientMessage);

            _server.Send(info.IPAndRemoteEndPoint, EncryptExtensions.Encrypt(json, "RDeds2442"));
        }

        public void MultiCasting(string message)
        {
            Parallel.ForEach(_clientList, async client =>
            {
                var resultBlocked = (from x in BlockedIPList where x == client.IPAddress select x).FirstOrDefault();
                //Server null değilse ve blonklanmış değilse gönder.

                MessageModel m = new MessageModel();
                m.Type = Library.MessageModel.Type.Message;
                m.Message = message;
                string json = JsonConvert.SerializeObject(m);

                if (_server != null && resultBlocked == null)
                {
                    if (_key == null)
                    {
                        await _server.SendAsync(client.IPAndRemoteEndPoint, Encoding.UTF8.GetBytes(json));
                    }
                    else
                    {
                        await _server.SendAsync(client.IPAndRemoteEndPoint, EncryptExtensions.Encrypt(json, _key));
                    }
                }
            });
        }
        #endregion
        #region Private Methods
        private bool ClientConnected(string ipPort)
        {
            return true;
        }
        private bool ClientDisconnected(string ipPort)
        {
            return true;
        }
        private bool MessageReceived(string ipPort, byte[] data)
        {
            string receiveData;

            if (_key == null)
                receiveData = Encoding.UTF8.GetString(data);
            else
            {
                byte[] decryptedData = DecryptExtensions.Decrypt(Convert.ToBase64String(data), _key);
                if (decryptedData == null)
                    return false;
                receiveData = Encoding.UTF8.GetString(decryptedData);
            }

            var message = JsonConvert.DeserializeObject<MessageModel>(receiveData);
            string[] split = ipPort.Split(':');
            string ipAddress = split[0];
            int port = Convert.ToInt32(split[1]);

            if (message != null)
            {
                ClientInfo client;
                client.IPAddress = IPAddress.Parse(ipAddress);
                client.RemoteEndPoint = port;
                client.IPAndRemoteEndPoint = ipPort;
                client.PCName = message.PCName;

                var resultBlocked = (from x in BlockedIPList where x == client.IPAddress select x).FirstOrDefault();
                if (resultBlocked != null)
                {
                    MessageModel blockedMessage = new MessageModel();
                    blockedMessage.Type = Library.MessageModel.Type.Blocked;
                    string json = JsonConvert.SerializeObject(blockedMessage);

                    if (_key == null)
                        _server.Send(client.IPAndRemoteEndPoint, Encoding.UTF8.GetBytes(json));

                    else
                        _server.Send(client.IPAndRemoteEndPoint, EncryptExtensions.Encrypt(json, _key));

                    return true;
                }
                if (message.Type == Library.MessageModel.Type.ClientLogon)
                {
                    lock (_LockOfClientList)
                    {
                        _clientList.Add(client);

                        MessageModel accecptMessage = new MessageModel();
                        accecptMessage.Type = Library.MessageModel.Type.AcceptLogon;
                        string json = JsonConvert.SerializeObject(accecptMessage);

                        if (_key == null)
                        {
                            bool status = _server.Send(client.IPAndRemoteEndPoint, EncryptExtensions.Encrypt(json, _key));
                            if (status) ConnectedClient?.Invoke(client);
                        }
                        else
                        {
                            bool status = _server.Send(client.IPAndRemoteEndPoint, EncryptExtensions.Encrypt(json, _key));
                            if (status) ConnectedClient?.Invoke(client);
                        }
                    }
                }

                if (message.Type ==Library.MessageModel.Type.Message)
                    ReceivedMessageFromClient?.Invoke(client, message.Message);
            }
            return true;
        }
        private void ClientListen()
        {
            while (true)
            {
                if (true)
                {
                    Parallel.ForEach(_clientList.ToList(), async (client) =>
                     {
                         bool status = _server != null && await _server.SendAsync(client.IPAndRemoteEndPoint, new byte[] { });

                         if (!status)
                         {
                             if (_server != null)
                                 _server.DisconnectClient(client.IPAndRemoteEndPoint);

                             lock (_LockOfClientList)
                             {
                                 if(_clientList.Count > 0)
                                 {
                                     _clientList.Remove(client);
                                     DisconnectedClient?.Invoke(client);
                                 }
                             }
                         }
                     });
                }
                Thread.Sleep(1000);
            }
        }

        #endregion
    }
}
