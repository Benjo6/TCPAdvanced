using Library.MetaData;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Library.Tcp
{
    public class Tcp_Server
    {
        #region Variables
        private bool _disposed = false;
        private bool _debug;
        private string _listenerIP;
        private int _listenerPort;
        private IPAddress _listenerIPAddress;
        private TcpListener _listener;
        private int _activeClients;
        private ConcurrentDictionary<string, ClientMetaData> _clients;
        private List<string> __permittedIPs;
        private CancellationTokenSource _tokenSource;
        private CancellationToken _token;
        private Func<string, bool> _clientConnected = null;
        private Func<string, bool> _clientDisconnected = null;
        private Func<string, byte[], bool> _messageReceived = null;
        #endregion

        #region Constructors

        public Tcp_Server(
             string listenerIp,
             int listenerPort,
             Func<string, bool> clientConnected,
             Func<string, bool> clientDisconnected,
             Func<string, byte[], bool> messageReceived,
             bool debug)
             : this(listenerIp, listenerPort, null, clientConnected, clientDisconnected, messageReceived, debug)
        {
        }

        public Tcp_Server(
            string listenerIp,
            int listenerPort,
            IEnumerable<string> permittedIps,
            Func<string, bool> clientConnected,
            Func<string, bool> clientDisconnected,
            Func<string, byte[], bool> messageReceived,
            bool debug)
        {
            if (listenerPort < 1)
                throw new ArgumentOutOfRangeException(nameof(listenerPort));

            _clientConnected = clientConnected;
            _clientDisconnected = clientDisconnected;
            _messageReceived = messageReceived ?? throw new ArgumentNullException(nameof(_messageReceived));

            _debug = debug;
            if (permittedIps != null && permittedIps.Count() > 0)
                __permittedIPs = new List<string>(permittedIps);

            if (String.IsNullOrEmpty(listenerIp))
            {
                _listenerIPAddress = IPAddress.Any;
                _listenerIP = _listenerIPAddress.ToString();
            }
            else
            {
                _listenerIPAddress = IPAddress.Parse(listenerIp);
                _listenerIP = listenerIp;
            }

            _listenerPort = listenerPort;
            Log("TcpServer starting on " + _listenerIP + ":" + _listenerPort);
            _listener = new TcpListener(_listenerIPAddress, _listenerPort);
            _tokenSource = new CancellationTokenSource();
            _activeClients = 0;
            _clients = new ConcurrentDictionary<string, ClientMetaData>();

            Task.Run(() => AcceptConnections(), _token);

        }
        #endregion

        #region Public Methods
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public bool Send(string ipPort, byte[] data)
        {
            if (!_clients.TryGetValue(ipPort, out ClientMetaData clientMetaData))
            {
                Log("*** Send unable to find client " + ipPort);
                return false;
            }
            return MessageWrite(clientMetaData, data);
        }
        public async Task<bool> SendAsync(string ipPort, byte[] data)
        {
            if(!_clients.TryGetValue(ipPort,out ClientMetaData clientMetaData))
            {
                Log("*** SendAsync unable to find client " + ipPort);
                return false;
            }

            return await MessageWriteAsync(clientMetaData, data);
        }
        public List<string> ListClients()
        {
            Dictionary<string, ClientMetaData> clients = _clients.ToDictionary(kvp=>kvp.Key,kvp => kvp.Value);
            List<string> list = new List<string>();
            foreach (KeyValuePair<string,ClientMetaData> item in clients)
                list.Add(item.Key);
            return list;
        }
        public void DisconnectClient(string ipPort)
        {
            if(!_clients.TryGetValue(ipPort,out ClientMetaData clientMetaData))
                Log("*** DisconnectClient unable to find client "+ipPort);
            
            else
                clientMetaData.Dispose();
        }
        #endregion
        #region Private Methods
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _tokenSource.Cancel();
                _tokenSource.Dispose();
                if (_listener != null && _listener.Server != null)
                {
                    _listener.Server.Close();
                    _listener.Server.Dispose();
                }
                if (_clients != null && _clients.Count > 0)
                {
                    foreach (KeyValuePair<string, ClientMetaData> metadata in _clients)
                    {
                        metadata.Value.Dispose();
                    }
                }
            }
            _disposed = true;
        }

        private void Log(string message)
        {
            if (_debug)
            {
                Console.WriteLine(message);
            }
        }
        private void LogException(string method, Exception exception)
        {
            Log("================================================================================");
            Log(" = Method: " + method);
            Log(" = Exception Type: " + exception.GetType().ToString());
            Log(" = Exception Data: " + exception.Data);
            Log(" = Inner Exception: " + exception.InnerException);
            Log(" = Exception Message: " + exception.Message);
            Log(" = Exception Source: " + exception.Source);
            Log(" = Exception StackTrace: " + exception.StackTrace);
            Log("================================================================================");
        }
        private string BytesToHex(byte[] data)
        {
            if (data == null || data.Length < 1)
                return "(null)";
            return BitConverter.ToString(data).Replace("-", "");
        }
        private async void  AcceptConnections()
        {
            _listener.Start();
            while (!_token.IsCancellationRequested)
            {
                string clientIPPort = String.Empty;
                try
                {
                    #region Accept-Connection

                    TcpClient tcpClient = await _listener.AcceptTcpClientAsync();
                    tcpClient.LingerState.Enabled = false;

                    #endregion

                    #region Get-Tuple-and-Check-IP

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                    string clientIp = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.

                    if (__permittedIPs != null && __permittedIPs.Count > 0)
                    {
                        if (!__permittedIPs.Contains(clientIp))
                        {
                            Log("*** AcceptConnections rejecting connection from " + clientIp + " (not permitted)");
                            tcpClient.Close();
                            continue;
                        }
                    }

                    #endregion

                    ClientMetaData client = new ClientMetaData(tcpClient);
                    clientIPPort = client.IPPort;

                    Log("*** AcceptConnections accepted connection from " + client.IPPort);

                    Task unawaited = Task.Run(() =>
                    {
                        FinalizeConnection(client);
                    }, _token);
                }
                catch (ObjectDisposedException ex)
                {
                    // Listener stopped ? if so, clientIpPort will be empty
                    Log("*** AcceptConnections ObjectDisposedException from " + clientIPPort + Environment.NewLine + ex.ToString());
                }
                catch (SocketException ex)
                {
                    switch (ex.Message)
                    {
                        case "An existing connection was forcibly closed by the remote host":
                            Log("*** AcceptConnections SocketException " + clientIPPort + " closed the connection.");
                            break;
                        default:
                            Log("*** AcceptConnections SocketException from " + clientIPPort + Environment.NewLine + ex.ToString());
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log("*** AcceptConnections Exception from " + clientIPPort + Environment.NewLine + ex.ToString());
                }
            }
        }

        private void FinalizeConnection(ClientMetaData client)
        {
            #region Add to client list
            if (!AddClient(client))
            {
                Log("*** FinalizeConnection unable to add client " + client.IPPort);
                client.Dispose();
                return;
            }
            int activeCount = Interlocked.Increment(ref _activeClients);
            #endregion
            #region Start data receiver
            Log("*** FinalizeConnection starting data receiver for " + client.IPPort + " (now " + activeCount + " clients)");
            if (_clientConnected != null)
                Task.Run(() => _clientConnected(client.IPPort));
            Task.Run(async () => await DataReceiver(client));
            #endregion
        }
        #endregion
        private bool IsConnected(ClientMetaData client)
        {
            if (client.TcpClient.Connected)
            {
                if ((client.TcpClient.Client.Poll(0, SelectMode.SelectWrite)) && (!client.TcpClient.Client.Poll(0, SelectMode.SelectError)))
                {
                    byte[] buffer = new byte[1];
                    if (client.TcpClient.Client.Receive(buffer, SocketFlags.Peek) == 0)
                    {
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private async Task DataReceiver(ClientMetaData client)
        {
            try
            {
                #region Wait-for-Data

                while (true)
                {
                    try
                    {
                        if (!IsConnected(client))
                        {
                            break;
                        }

                        byte[] data = await MessageReadAsync(client);
                        if (data == null)
                        {
                            // no message available
                            await Task.Delay(30);
                            continue;
                        }

                        if (_messageReceived != null)
                        {
                            Task<bool> unawaited = Task.Run(() => _messageReceived(client.IPPort, data));
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }

                #endregion
            }
            finally
            {
                int activeCount = Interlocked.Decrement(ref _activeClients);
                RemoveClient(client);
                if (_clientDisconnected != null)
                {
                    Task<bool> unawaited = Task.Run(() => _clientDisconnected(client.IPPort));
                }
                Log("*** DataReceiver client " + client.IPPort + " disconnected (now " + activeCount + " clients active)");

                client.Dispose();
            }
        }

        private bool AddClient(ClientMetaData client)
        {
            if (!_clients.TryRemove(client.IPPort, out ClientMetaData removedClient))
            {
                // do nothing, it probably did not exist anyway
            }

            _clients.TryAdd(client.IPPort, client);
            Log("*** AddClient added client " + client.IPPort);
            return true;
        }

        private bool RemoveClient(ClientMetaData client)
        {
            if (!_clients.TryRemove(client.IPPort, out ClientMetaData removedClient))
            {
                Log("*** RemoveClient unable to remove client " + client.IPPort);
                return false;
            }
            else
            {
                Log("*** RemoveClient removed client " + client.IPPort);
                return true;
            }
        }

        private byte[] MessageRead(ClientMetaData client)
        {
            #region Variables

            int bytesRead = 0;
            int sleepInterval = 25;
            int maxTimeout = 500;
            int currentTimeout = 0;
            bool timeout = false;

            byte[] headerBytes;
            string header = "";
            long contentLength;
            byte[] contentBytes;

            if (!client.NetworkStream.CanRead)
            {
                return null;
            }

            if (!client.NetworkStream.DataAvailable)
            {
                return null;
            }

            #endregion

            #region Read Header

            using (MemoryStream headerMs = new MemoryStream())
            {
                #region Read-Header-Bytes

                byte[] headerBuffer = new byte[1];
                timeout = false;
                currentTimeout = 0;
                int read = 0;

                while ((read = client.NetworkStream.ReadAsync(headerBuffer, 0, headerBuffer.Length).Result) > 0)
                {
                    if (read > 0)
                    {
                        headerMs.Write(headerBuffer, 0, read);
                        bytesRead += read;
                        currentTimeout = 0;

                        if (bytesRead > 1)
                        {
                            // check if end of headers reached
                            if (headerBuffer[0] == 58)
                            {
                                break;
                            }
                        }
                        else
                        {
                            if (currentTimeout >= maxTimeout)
                            {
                                timeout = true;
                                break;
                            }
                            else
                            {
                                currentTimeout += sleepInterval;
                                Task.Delay(sleepInterval).Wait();
                            }
                        }
                    }
                }

                if (timeout)
                {
                    Log("*** MessageRead timeout " + currentTimeout + "ms/" + maxTimeout + "ms exceeded while reading header after reading " + bytesRead + " bytes");
                    return null;
                }

                headerBytes = headerMs.ToArray();
                if (headerBytes == null || headerBytes.Length < 1)
                {
                    return null;
                }

                #endregion

                #region Process-Header

                header = Encoding.UTF8.GetString(headerBytes);
                header = header.Replace(":", "");

                if (!Int64.TryParse(header, out contentLength))
                {
                    Log("*** MessageRead malformed message from " + client.IPPort + " (message header not an integer)");
                    return null;
                }

                #endregion
            }

            #endregion

            #region Read Data

            using (MemoryStream dataMs = new MemoryStream())
            {
                long bytesRemaining = contentLength;
                timeout = false;
                currentTimeout = 0;

                int read = 0;
                byte[] buffer;
                long bufferSize = 2048;
                if (bufferSize > bytesRemaining)
                {
                    bufferSize = bytesRemaining;
                }

                buffer = new byte[bufferSize];

                while ((read = client.NetworkStream.ReadAsync(buffer, 0, buffer.Length).Result) > 0)
                {
                    if (read > 0)
                    {
                        dataMs.Write(buffer, 0, read);
                        bytesRead = bytesRead + read;
                        bytesRemaining = bytesRemaining - read;
                        currentTimeout = 0;

                        // reduce buffer size if number of bytes remaining is
                        // less than the pre-defined buffer size of 2KB
                        if (bytesRemaining < bufferSize)
                        {
                            bufferSize = bytesRemaining;
                        }

                        buffer = new byte[bufferSize];

                        // check if read fully
                        if (bytesRemaining == 0)
                        {
                            break;
                        }

                        if (bytesRead == contentLength)
                        {
                            break;
                        }
                    }
                    else
                    {
                        if (currentTimeout >= maxTimeout)
                        {
                            timeout = true;
                            break;
                        }
                        else
                        {
                            currentTimeout += sleepInterval;
                            Task.Delay(sleepInterval).Wait();
                        }
                    }
                }

                if (timeout)
                {
                    Log("*** MessageRead timeout " + currentTimeout + "ms/" + maxTimeout + "ms exceeded while reading content after reading " + bytesRead + " bytes");
                    return null;
                }

                contentBytes = dataMs.ToArray();
            }

            #endregion

            #region Check Content Bytes

            if (contentBytes == null || contentBytes.Length < 1)
            {
                Log("*** MessageRead " + client.IPPort + " no content read");
                return null;
            }

            if (contentBytes.Length != contentLength)
            {
                Log("*** MessageRead " + client.IPPort + " content length " + contentBytes.Length + " bytes does not match header value " + contentLength + ", discarding");
                return null;
            }

            #endregion

            return contentBytes;
        }

        private async Task<byte[]> MessageReadAsync(ClientMetaData client)
        {
            /*
             *
             * Do not catch exceptions, let them get caught by the data reader
             * to destroy the connection
             *
             */

            #region Variables

            int bytesRead = 0;
            int sleepInterval = 25;
            int maxTimeout = 500;
            int currentTimeout = 0;
            bool timeout = false;

            byte[] headerBytes;
            string header = "";
            long contentLength;
            byte[] contentBytes;

            if (!client.NetworkStream.CanRead)
            {
                return null;
            }

            if (!client.NetworkStream.DataAvailable)
            {
                return null;
            }

            #endregion

            #region Read-Header

            using (MemoryStream headerMs = new MemoryStream())
            {
                #region Read-Header-Bytes

                byte[] headerBuffer = new byte[1];
                timeout = false;
                currentTimeout = 0;
                int read = 0;

                while ((read = await client.NetworkStream.ReadAsync(headerBuffer, 0, headerBuffer.Length)) > 0)
                {
                    if (read > 0)
                    {
                        await headerMs.WriteAsync(headerBuffer, 0, read);
                        bytesRead += read;

                        // reset timeout since there was a successful read
                        currentTimeout = 0;
                    }
                    else
                    {
                        #region Check for Timeout

                        if (currentTimeout >= maxTimeout)
                        {
                            timeout = true;
                            break;
                        }
                        else
                        {
                            currentTimeout += sleepInterval;
                            await Task.Delay(sleepInterval);
                        }

                        if (timeout)
                        {
                            break;
                        }

                        #endregion
                    }

                    if (bytesRead > 1)
                    {
                        // check if end of headers reached
                        if (headerBuffer[0] == 58)
                        {
                            break;
                        }
                    }
                    else
                    {
                        #region Check for Timeout

                        if (currentTimeout >= maxTimeout)
                        {
                            timeout = true;
                            break;
                        }
                        else
                        {
                            currentTimeout += sleepInterval;
                            await Task.Delay(sleepInterval);
                        }

                        if (timeout)
                        {
                            break;
                        }

                        #endregion
                    }
                }

                if (timeout)
                {
                    Log("*** MessageReadAsync timeout " + currentTimeout + "ms/" + maxTimeout + "ms exceeded while reading header after reading " + bytesRead + " bytes");
                    return null;
                }

                headerBytes = headerMs.ToArray();
                if (headerBytes == null || headerBytes.Length < 1)
                {
                    return null;
                }

                #endregion

                #region Process Header

                header = Encoding.UTF8.GetString(headerBytes);
                header = header.Replace(":", "");

                if (!Int64.TryParse(header, out contentLength))
                {
                    Log("*** MessageReadAsync malformed message from " + client.IPPort + " (message header not an integer)");
                    return null;
                }

                #endregion
            }

            #endregion

            #region Read-Data

            using (MemoryStream dataMs = new MemoryStream())
            {
                long bytesRemaining = contentLength;
                timeout = false;
                currentTimeout = 0;

                int read = 0;
                byte[] buffer;
                long bufferSize = 2048;
                if (bufferSize > bytesRemaining)
                {
                    bufferSize = bytesRemaining;
                }

                buffer = new byte[bufferSize];

                while ((read = await client.NetworkStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    if (read > 0)
                    {
                        dataMs.Write(buffer, 0, read);
                        bytesRead = bytesRead + read;
                        bytesRemaining = bytesRemaining - read;

                        // reset timeout
                        currentTimeout = 0;

                        // reduce buffer size if number of bytes remaining is
                        // less than the pre-defined buffer size of 2KB
                        if (bytesRemaining < bufferSize)
                        {
                            bufferSize = bytesRemaining;
                        }

                        buffer = new byte[bufferSize];

                        // check if read fully
                        if (bytesRemaining == 0)
                        {
                            break;
                        }

                        if (bytesRead == contentLength)
                        {
                            break;
                        }
                    }
                    else
                    {
                        #region Check for Timeout

                        if (currentTimeout >= maxTimeout)
                        {
                            timeout = true;
                            break;
                        }
                        else
                        {
                            currentTimeout += sleepInterval;
                            await Task.Delay(sleepInterval);
                        }

                        if (timeout)
                        {
                            break;
                        }

                        #endregion
                    }
                }

                if (timeout)
                {
                    Log("*** MessageReadAsync timeout " + currentTimeout + "ms/" + maxTimeout + "ms exceeded while reading content after reading " + bytesRead + " bytes");
                    return null;
                }

                contentBytes = dataMs.ToArray();
            }

            #endregion

            #region Check Content Bytes

            if (contentBytes == null || contentBytes.Length < 1)
            {
                Log("*** MessageReadAsync " + client.IPPort + " no content read");
                return null;
            }

            if (contentBytes.Length != contentLength)
            {
                Log("*** MessageReadAsync " + client.IPPort + " content length " + contentBytes.Length + " bytes does not match header value " + contentLength + ", discarding");
                return null;
            }

            #endregion

            return contentBytes;
        }

        private bool MessageWrite(ClientMetaData client, byte[] data)
        {
            try
            {
                #region Format Message

                string header = "";
                byte[] headerBytes;
                byte[] message;

                if (data == null || data.Length < 1)
                {
                    header += "0:";
                }
                else
                {
                    header += data.Length + ":";
                }

                headerBytes = Encoding.UTF8.GetBytes(header);
                int messageLen = headerBytes.Length;
                if (data != null && data.Length > 0)
                {
                    messageLen += data.Length;
                }

                message = new byte[messageLen];
                Buffer.BlockCopy(headerBytes, 0, message, 0, headerBytes.Length);

                if (data != null && data.Length > 0)
                {
                    Buffer.BlockCopy(data, 0, message, headerBytes.Length, data.Length);
                }

                #endregion

                #region Send Message

                client.NetworkStream.Write(message, 0, message.Length);
                client.NetworkStream.Flush();
                return true;

                #endregion
            }
            catch (Exception)
            {
                Log("*** MessageWrite " + client.IPPort + " disconnected due to exception");
                return false;
            }
        }

        private async Task<bool> MessageWriteAsync(ClientMetaData client, byte[] data)
        {
            try
            {
                #region Format Message

                string header = "";
                byte[] headerBytes;
                byte[] message;

                if (data == null || data.Length < 1)
                {
                    header += "0:";
                }
                else
                {
                    header += data.Length + ":";
                }

                headerBytes = Encoding.UTF8.GetBytes(header);
                int messageLen = headerBytes.Length;
                if (data != null && data.Length > 0)
                {
                    messageLen += data.Length;
                }

                message = new byte[messageLen];
                Buffer.BlockCopy(headerBytes, 0, message, 0, headerBytes.Length);

                if (data != null && data.Length > 0)
                {
                    Buffer.BlockCopy(data, 0, message, headerBytes.Length, data.Length);
                }

                #endregion

                #region Send Message Async

                await client.NetworkStream.WriteAsync(message, 0, message.Length);
                await client.NetworkStream.FlushAsync();
                return true;

                #endregion
            }
            catch (Exception)
            {
                Log("*** MessageWriteAsync " + client.IPPort + " disconnected due to exception");
                return false;
            }
        } 
    }
}