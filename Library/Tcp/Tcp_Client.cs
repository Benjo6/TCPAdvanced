using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Library.Tcp
{
    public class Tcp_Client : IDisposable
    {

        #region Variables
        private bool _disposed = false;
        private string _sourceIP;
        private int _sourcePort;
        private string _serverIP;
        private int _serverPort;
        private bool _debug;
        private TcpClient _client;
        private bool _connected;
        private Func<byte[], bool> _messageReceived = null;
        private Func<bool> _serverConnected = null;
        private Func<bool> _serverDisconnected = null;
        private readonly SemaphoreSlim _sendLock;
        private CancellationTokenSource _tokenSource;
        private CancellationToken _token;


        #endregion

        #region Constructor
        public Tcp_Client(
             string serverIp,
             int serverPort,
             Func<bool> serverConnected,
             Func<bool> serverDisconnected,
             Func<byte[], bool> messageReceived,
             bool debug)
        {
            if (String.IsNullOrEmpty(serverIp))
            {
                throw new ArgumentNullException(nameof(serverIp));
            }

            if (serverPort < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(serverPort));
            }

            _serverIP = serverIp;
            _serverPort = serverPort;

            _serverConnected = serverConnected;
            _serverDisconnected = serverDisconnected;
            _messageReceived = messageReceived ?? throw new ArgumentNullException(nameof(messageReceived));

            _debug = debug;

            _sendLock = new SemaphoreSlim(1);

            _client = new TcpClient();
            IAsyncResult ar = _client.BeginConnect(_serverIP, _serverPort, null, null);
            WaitHandle wh = ar.AsyncWaitHandle;

            try
            {
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5), false))
                {
                    _client.Close();
                    throw new TimeoutException("Timeout connecting to " + _serverIP + ":" + _serverPort);
                }

                _client.EndConnect(ar);

                _serverIP = ((IPEndPoint)_client.Client.LocalEndPoint).Address.ToString();
                _sourcePort = ((IPEndPoint)_client.Client.LocalEndPoint).Port;
                LocalAddress = ((IPEndPoint)_client.Client.LocalEndPoint).Address.ToString();
                LocalPort = ((IPEndPoint)_client.Client.LocalEndPoint).Port.ToString();
                _connected = true;
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                wh.Close();
            }

            if (_serverConnected != null)
            {
                Task.Run(() => _serverConnected());
            }

            _tokenSource = new CancellationTokenSource();
            _token= _tokenSource.Token;
            Task.Run(async () => await DataReceiver(_token), _token);
        }
        #endregion

        #region Properties
        public string LocalAddress { get; set; }
        public string LocalPort { get; set; }
        #endregion

        #region Public Methods
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public bool Send(byte[] data)
        {
            return MessageWrite(data);
        }

        public async Task<bool> SendAsync(byte[] data)
        {
            return await MessageWriteAsync(data);
        }

        public bool IsConnected()
        {
            return _connected;
        }
        #endregion

        #region Private Methods
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_client != null)
                {
                    if (_client.Connected)
                    {
                        NetworkStream ns = _client.GetStream();
                        if (ns != null)
                            ns.Close();
                    }
                    _client.Close();
                }
                _tokenSource.Cancel();
                _tokenSource.Dispose();
                _sendLock.Dispose();
                _connected = false;
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
            Log(" = Exeception Data: " + exception.Data);
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


        private async Task DataReceiver(CancellationToken? cancellationToken = null)
        {
            try
            {
                #region Wait-for-Data

                while (true)
                {
                    cancellationToken?.ThrowIfCancellationRequested();

                    #region Check-if-Client-Connected-to-Server

                    if (_client == null)
                    {
                        Log("*** DataReceiver null TCP interface detected, disconnection or close assumed");
                        break;
                    }

                    if (!_client.Connected)
                    {
                        Log("*** DataReceiver server disconnected");
                        break;
                    }

                    #endregion

                    #region Read-Message-and-Handle

                    byte[] data = await MessageReadAsync();
                    if (data == null)
                    {
                        await Task.Delay(30);
                        continue;
                    }

                    Task<bool> unawaited = Task.Run(() => _messageReceived(data));

                    #endregion
                }

                #endregion
            }
            catch (OperationCanceledException)
            {
                throw; // normal cancellation
            }
            catch (Exception)
            {
                Log("*** DataReceiver server disconnected");
            }
            finally
            {
                _connected = false;
                _serverDisconnected?.Invoke();
            }
        }

        private byte[] MessageRead()
        {
            string sourceIp = "";
            int sourcePort = 0;

            try
            {
                #region Check-for-Null-Values

                if (_client == null)
                {
                    Log("*** MessageRead null client supplied");
                    return null;
                }

                if (!_client.Connected)
                {
                    Log("*** MessageRead supplied client is not connected");
                    return null;
                }

                #endregion

                #region Variables

                int bytesRead = 0;
                int sleepInterval = 25;
                int maxTimeout = 500;
                int currentTimeout = 0;
                bool timeout = false;

                sourceIp = ((IPEndPoint)_client.Client.RemoteEndPoint).Address.ToString();
                sourcePort = ((IPEndPoint)_client.Client.RemoteEndPoint).Port;
                NetworkStream ClientStream = null;

                try
                {
                    ClientStream = _client.GetStream();
                }
                catch (Exception exception)
                {
                    Log("*** MessageRead disconnected while attaching to stream: " + exception.Message);
                    return null;
                }

                byte[] headerBytes;
                string header = "";
                long contentLength;
                byte[] contentBytes;

                #endregion

                #region Read Header

                if (!ClientStream.CanRead && !ClientStream.DataAvailable)
                {
                    return null;
                }

                using (MemoryStream headerMs = new MemoryStream())
                {
                    #region Read Header Bytes

                    byte[] headerBuffer = new byte[1];
                    timeout = false;
                    currentTimeout = 0;
                    int read = 0;

                    while ((read = ClientStream.ReadAsync(headerBuffer, 0, headerBuffer.Length).Result) > 0)
                    {
                        if (read > 0)
                        {
                            headerMs.Write(headerBuffer, 0, read);
                            bytesRead += read;
                            currentTimeout = 0;

                            if (bytesRead > 1)
                            {
                                if (headerBuffer[0] == 58)
                                {
                                    break;
                                }
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
                        Log("*** MessageRead timeout " + currentTimeout + "ms/" + maxTimeout + "ms exceeded while reading header after reading " + bytesRead + " bytes");
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
                        Log("*** MessageRead malformed message from server (message header not an integer)");
                        return null;
                    }

                    #endregion
                }

                #endregion

                #region Read Data

                using (MemoryStream ms = new MemoryStream())
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

                    while ((read = ClientStream.ReadAsync(buffer, 0, buffer.Length).Result) > 0)
                    {
                        if (read > 0)
                        {
                            ms.Write(buffer, 0, read);
                            bytesRead = bytesRead + read;
                            bytesRemaining = bytesRemaining - read;

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

                    contentBytes = ms.ToArray();
                }

                #endregion

                #region Check Content Bytes

                if (contentBytes == null || contentBytes.Length < 1)
                {
                    Log("*** MessageRead no content read");
                    return null;
                }

                if (contentBytes.Length != contentLength)
                {
                    Log("*** MessageRead content length " + contentBytes.Length + " bytes does not match header value of " + contentLength);
                    return null;
                }

                #endregion

                return contentBytes;
            }
            catch (Exception)
            {
                Log("*** MessageRead server disconnected");
                return null;
            }
        }
        private async Task<byte[]> MessageReadAsync()
        {
            string sourceIP = "";
            int sourcePort = 0;

            try
            {
                #region Check for Null Values
                if (_client == null)
                {
                    Log("*** MessageReadAsync null client supplied");
                    return null;
                }
                if (!_client.Connected)
                {
                    Log("*** MessageReadAsync supplied client is not connected");
                    return null;
                }
                #endregion

                #region Variables
                int bytesRead = 0;
                int sleepInterval = 25;
                int maxTimeout = 500;
                int currentTimeout = 0;
                bool timeout = false;

                sourceIP = ((IPEndPoint)_client.Client.RemoteEndPoint).Address.ToString();
                sourcePort = ((IPEndPoint)_client.Client.RemoteEndPoint).Port;
                LocalAddress = ((IPEndPoint)_client.Client.LocalEndPoint).Address.ToString();
                LocalPort = ((IPEndPoint)_client.Client.LocalEndPoint).Port.ToString();
                NetworkStream ClientStream = null;

                try
                {
                    ClientStream = _client.GetStream();
                }
                catch (Exception exception)
                {
                    Log("*** MessageRead disconnected while attaching to stream: " + exception.Message);
                    return null;
                }

                #endregion
                #region Read Header

                byte[] headerBytes;
                string header = "";
                long contentLength;
                byte[] contentBytes;
                if (!ClientStream.CanRead && !ClientStream.DataAvailable)
                {
                    return null;
                }

                using (MemoryStream headerMs = new MemoryStream())
                {
                    #region Read-Header-Bytes

                    byte[] headerBuffer = new byte[1];
                    timeout = false;
                    currentTimeout = 0;
                    int read = 0;

                    while ((read = await ClientStream.ReadAsync(headerBuffer, 0, headerBuffer.Length)) > 0)
                    {
                        if (read > 0)
                        {
                            await headerMs.WriteAsync(headerBuffer, 0, read);
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
                                await Task.Delay(sleepInterval);
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
                        Log("*** MessageRead malformed message from server (message header not an integer)");
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

                    while ((read = await ClientStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (read > 0)
                        {
                            await dataMs.WriteAsync(buffer, 0, read);
                            bytesRead = bytesRead + read;
                            bytesRemaining = bytesRemaining - read;

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
                                await Task.Delay(sleepInterval);
                            }
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

                #region Check-Content-Bytes

                if (contentBytes == null || contentBytes.Length < 1)
                {
                    Log("*** MessageRead no content read");
                    return null;
                }

                if (contentBytes.Length != contentLength)
                {
                    Log("*** MessageRead content length " + contentBytes.Length + " bytes does not match header value of " + contentLength);
                    return null;
                }

                #endregion

                return contentBytes;
            }
            catch (Exception)
            {
                Log("*** MessageRead server disconnected");
                return null;
            }
        }
        private bool MessageWrite(byte[] data)
        {
            bool disconnectedDetected = false;

            try
            {
                #region Check if connected
                if (_client == null)
                {
                    Log("MessageWrite client is null");
                    disconnectedDetected = true;
                    return false;
                }
                #endregion

                #region Format Message
                string header = "";
                byte[] headerBytes;
                byte[] message;

                if (data == null || data.Length < 1)
                    header += "0:";
                else
                    header += data.Length + ":";

                headerBytes = Encoding.UTF8.GetBytes(header);
                int messageLen = headerBytes.Length;
                if (data != null && data.Length > 0)
                    messageLen += data.Length;
                
                message=new byte[messageLen];

                Buffer.BlockCopy(headerBytes, 0, message, 0, headerBytes.Length);

                if(data!= null&&data.Length>0)
                    Buffer.BlockCopy(data,0,message,headerBytes.Length,data.Length);

                #endregion

                #region Send Message

                _sendLock.Wait();

                try
                {
                    _client.GetStream().Write(message,0,message.Length);
                    _client.GetStream().Flush();
                }
                finally
                {
                    _sendLock?.Release();  
                }
                return true;
            }
            catch (ObjectDisposedException ObjDispInner)
            {
                Log("*** MessageWrite server disconnected (obj disposed exception): " + ObjDispInner.Message);
                disconnectedDetected = true;
                return false;
            }
            catch (SocketException SockInner)
            {
                Log("*** MessageWrite server disconnected (socket exception): " + SockInner.Message);
                disconnectedDetected = true; 
                return false;
            }
            catch (InvalidOperationException InvOpInner)
            {
                Log("*** MessageWrite server disconnected (invalid operation exception): " + InvOpInner.Message);
                disconnectedDetected = true;
                return false;
            }
            catch (IOException IOInner)
            {
                Log("*** MessageWrite server disconnected (IO exception): " + IOInner.Message);
                disconnectedDetected = true;
                return false;
            }
            catch (Exception e)
            {
                LogException("MessageWrite", e);
                disconnectedDetected = true;
                return false;
            }
            finally
            {
                if (disconnectedDetected)
                {
                    _connected = false;
                    Dispose();
                }
            }
        }

        private async Task<bool> MessageWriteAsync(byte[] data)
        {
            bool disconnectDetected = false;

            try
            {
                #region Check-if-Connected

                if (_client == null)
                {
                    Log("MessageWriteAsync client is null");
                    disconnectDetected = true;
                    return false;
                }

                #endregion

                #region Format-Message

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

                #region Send-Message

                await _sendLock.WaitAsync();
                try
                {
                    _client.GetStream().Write(message, 0, message.Length);
                    _client.GetStream().Flush();
                }
                finally
                {
                    _sendLock.Release();
                }

                return true;

                #endregion
            }
            catch (ObjectDisposedException ObjDispInner)
            {
                Log("*** MessageWriteAsync server disconnected (obj disposed exception): " + ObjDispInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (SocketException SockInner)
            {
                Log("*** MessageWriteAsync server disconnected (socket exception): " + SockInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (InvalidOperationException InvOpInner)
            {
                Log("*** MessageWriteAsync server disconnected (invalid operation exception): " + InvOpInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (IOException IOInner)
            {
                Log("*** MessageWriteAsync server disconnected (IO exception): " + IOInner.Message);
                disconnectDetected = true;
                return false;
            }
            catch (Exception e)
            {
                LogException("MessageWriteAsync", e);
                disconnectDetected = true;
                return false;
            }
            finally
            {
                if (disconnectDetected)
                {
                    _connected = false;
                    Dispose();
                }
            }
        }
    }
    #endregion
    #endregion
}