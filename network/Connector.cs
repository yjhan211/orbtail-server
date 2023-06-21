#pragma warning disable CS8618
#pragma warning disable CS8622

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
        public ConnectHandler connected_callback { get; set; }

        Socket client;

        readonly NetworkService network_service;

        public Connector(NetworkService network_service)
        {
            this.network_service = network_service;
        }

        public void Connect(IPEndPoint remote_endpoint)
        {
            this.client = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp
            );

            SocketAsyncEventArgs event_arg = new();
            event_arg.Completed += OnConnectCompleted;
            event_arg.RemoteEndPoint = remote_endpoint;
            if (!this.client.ConnectAsync(event_arg))
            {
                OnConnectCompleted(null, event_arg);
            }
        }

        void OnConnectCompleted(object? sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success)
            {
                Console.WriteLine(string.Format("Failed to connect. {0}", args.SocketError));
                return;
            }

            UserToken token = new();
            this.network_service.onConnectCompleted(this.client, token);
            this.connected_callback(token);
        }
    }
}
