using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public class Connector
    {
        public delegate void ConnectHandler(UserToken token);
        public ConnectHandler connectedCallback { get; set; }

        Socket client;

        NetworkService network_service;

        public Connector(NetworkService network_service)
        {
            this.network_service = network_service;
        }

        public void connect(IPEndPoint remote_endpoint)
        {
            this.client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            SocketAsyncEventArgs event_arg = new SocketAsyncEventArgs();
            event_arg.Completed += onConnectCompleted;
            event_arg.RemoteEndPoint = remote_endpoint;
            if (!this.client.ConnectAsync(event_arg))
            {
                onConnectCompleted(null, event_arg);
            }
        }

        void onConnectCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success)
            {
                Console.WriteLine(string.Format("Failed to connect. {0}", args.SocketError));
                return;
            }

            UserToken token = new UserToken();
            this.network_service.onConnectCompleted(this.client, token);
            this.connectedCallback(token);
        }
    }
}
