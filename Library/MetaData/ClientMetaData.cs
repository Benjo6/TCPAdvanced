using System.Net.Security;
using System.Net.Sockets;

namespace Library.MetaData
{
    public class ClientMetaData
    {
        #region Variables

        private bool _disposed = false;

        private TcpClient _tcpClient;
        private NetworkStream _networkStream;
        private SslStream _sslStream;
        private string _ipPort;

        #endregion

        #region Constructors

        public ClientMetaData(TcpClient tcpClient)
        {
            _tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            _networkStream = tcpClient.GetStream();
            _ipPort = tcpClient.Client.RemoteEndPoint.ToString();
        }
        #endregion
        #region Properties

        public TcpClient TcpClient
        {
            get { return _tcpClient; }
        }
        public NetworkStream NetworkStream
        {
            get { return _networkStream; }
        }
        public SslStream SslStream { get { return _sslStream; } set { _sslStream = value; } }
        public string IPPort { get { return _ipPort; } }


        #endregion

        #region Public Methods
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion

        #region Private Methods
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_sslStream != null)
                    _sslStream.Close();
                if (_networkStream != null)
                    _networkStream.Close();
                if (_tcpClient != null)
                    _tcpClient.Close();
            }
            _disposed = true;
        }


        #endregion

    }
}
