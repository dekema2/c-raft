#region C#raft License
// This file is part of C#raft. Copyright C#raft Team 
// 
// C#raft is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
#endregion
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Chraft.Entity;
using Chraft.Net.Packets;
using Chraft.Plugins.Events;
using Chraft.World;
using Chraft.Utils;
using Chraft.Plugins.Events.Args;

namespace Chraft.Net
{
    public partial class Client
    {
        internal const int ProtocolVersion = 17;
        private readonly Socket _socket;
        public volatile bool Running = true;
        public PacketHandler PacketHandler { get; private set; }
        private Timer _keepAliveTimer;
        private Player _player = null;

        public static SocketAsyncEventArgsPool SendSocketEventPool = new SocketAsyncEventArgsPool(10);
        public static SocketAsyncEventArgsPool RecvSocketEventPool = new SocketAsyncEventArgsPool(10);
        public static BufferPool RecvBufferPool = new BufferPool("Receive", 2048, 2048);


        private byte[] _recvBuffer;
        private SocketAsyncEventArgs _sendSocketEvent;
        private SocketAsyncEventArgs _recvSocketEvent;

        private ByteQueue _currentBuffer;
        private ByteQueue _processedBuffer;
        private ByteQueue _fragPackets;

        private bool _sendSystemDisposed;
        private bool _recvSystemDisposed;

        private readonly object _disposeLock = new object();

        private DateTime _nextActivityCheck;

        public Server Server;

        internal int SessionID { get; private set; }

        /// <summary>
        /// The mixed-case, clean username of the client.
        /// </summary>
        public string Username { get; set; }

        public Player Owner
        {
            get { return _player; }
        }

        public ByteQueue FragPackets
        {
            get { return _fragPackets; }
            set { _fragPackets = value; }
        }

        /// <summary>
        /// A reference to the server logger.
        /// </summary>
        public Logger Logger { get { return Server.Logger; } }

        /// <summary>
        /// Instantiates a new Client object.
        /// </summary>
        internal Client(int sessionId, Server server, Socket socket)
        {
            _socket = socket;
            _currentBuffer = new ByteQueue();
            _processedBuffer = new ByteQueue();
            _fragPackets = new ByteQueue();
            _nextActivityCheck = DateTime.Now + TimeSpan.FromSeconds(10);
            SessionID = sessionId;
            Server = server;
            _chunkSendTimer = new Timer(SendChunks, null, Timeout.Infinite, Timeout.Infinite);
            //PacketHandler = new PacketHandler(Server, socket);
        }

        public bool CheckUsername(string username)
        {
            string usernameToCheck = Regex.Replace(username, Chat.DISALLOWED, "");
            Logger.Log(Logger.LogLevel.Debug, "Username: {0}", usernameToCheck);
            return usernameToCheck == Username;
        }

        private void SetGameMode()
        {
            SendPacket(new NewInvalidStatePacket
            {
                GameMode = _player.GameMode,
                Reason = NewInvalidStatePacket.NewInvalidReason.ChangeGameMode
            });
        }

        public void Start()
        {
            Running = true;
            _sendSocketEvent = SendSocketEventPool.Pop();
            _recvSocketEvent = RecvSocketEventPool.Pop();
            _recvBuffer = RecvBufferPool.AcquireBuffer();

            _recvSocketEvent.SetBuffer(_recvBuffer, 0, _recvBuffer.Length);
            _recvSocketEvent.Completed += new EventHandler<SocketAsyncEventArgs>(Recv_Completed);

            _sendSocketEvent.Completed += new EventHandler<SocketAsyncEventArgs>(Send_Completed);

            Task.Factory.StartNew(Recv_Start);
        }

        /*internal void AssociateInterface(Interface iface)
        {
            iface.PacketHandler = PacketHandler;
        }*/

        private void CloseInterface()
        {
            if (_player.CurrentInterface == null)
                return;
            SendPacket(new CloseWindowPacket
            {
                WindowId = _player.CurrentInterface.Handle
            });
        }

        public int Ping { get; set; }
        public int LastKeepAliveId;
        public DateTime KeepAliveStart;
        public DateTime LastClientResponse = DateTime.Now;

        private void KeepAliveTimer_Callback(object sender)
        {
            if (Running)
            {
                if ((DateTime.Now - LastClientResponse).TotalSeconds > 60)
                {
                    // Client hasn't sent or responded to a keepalive within 60secs
                    this.Stop();
                    return;
                }
                LastKeepAliveId = _player.Server.Rand.Next();
                KeepAliveStart = DateTime.Now;
                SendPacket(new KeepAlivePacket() {KeepAliveID = this.LastKeepAliveId});
            }
        }

        public void CheckAlive()
        {
            if(DateTime.Now > _nextActivityCheck)
                Stop();
        }

        /// <summary>
        /// Stop reading packets from the client, and kill the keep-alive timer.
        /// </summary>
        public void Stop()
        {
            MarkToDispose();
            DisposeRecvSystem();
            DisposeSendSystem();
        }

        /// <summary>
        /// Disconnect the client with the given reason.
        /// </summary>
        /// <param name="reason">The reason to be displayed to the player.</param>
        public void Kick(string reason)
        {
            //Event
            ClientKickedEventArgs e = new ClientKickedEventArgs(this, reason);
            Server.PluginManager.CallEvent(Event.PlayerKicked, e);
            if (e.EventCanceled) return;
            reason = e.Message;
            //End Event

            if(_player != null && _player.LoggedIn)
                Save();

            SendPacket(new DisconnectPacket
            {
                Reason = reason
            });
        }

        public void Disconnected(object sender, SocketAsyncEventArgs e)
        {
            if (_player != null && _player.LoggedIn)
                Save();
            // Just wait a bit since it's possible that we close the socket before the packet reached the client
            Thread.Sleep(200);
            Stop();
        }

        /// <summary>
        /// Disposes associated resources and stops the client.  Also removes the client from the server's client/entity lists.
        /// </summary>
        public void Dispose()
        {
            if (_player != null)
            {
                Server.Logger.Log(Chraft.Logger.LogLevel.Info, "Disposing {0}", _player.DisplayName);
                string disconnectMsg = ChatColor.Yellow + _player.DisplayName + " has left the game.";
                //Event
                ClientLeftEventArgs e = new ClientLeftEventArgs(this);
                Server.PluginManager.CallEvent(Plugins.Events.Event.PlayerLeft, e);
                //You cant stop the player from leaving so dont try.
                disconnectMsg = e.BrodcastMessage;
                //End Event

                if (_player.LoggedIn)
                {
                    _player.Server.BroadcastSync(disconnectMsg, this);
                    Save();
                }

                Task.Factory.StartNew(() =>
                {
                    foreach (Chunk chunk in _player.LoadedChunks.Values)
                    {
                        if (chunk != null)
                            chunk.RemoveClient(this);
                    }
                });

                Server.RemoveAuthenticatedClient(this);

                Server.Logger.Log(Chraft.Logger.LogLevel.Info, "Clients online: {0}", Server.Clients.Count);
                Server.RemoveEntity(_player, false);

                Client[] nearbyClients = Server.GetNearbyPlayers(_player.World, UniversalCoords.FromAbsWorld(_player.Position)).ToArray();

                foreach (var client in nearbyClients)
                {
                    if (client != this)
                    {
                        DestroyEntityPacket de = new DestroyEntityPacket {EntityId = _player.EntityId};
                        de.Write();
                        byte[] data = de.GetBuffer();
                        client.Send_Sync(data);
                    }
                }

                _player.LoggedIn = false;
                _player.Ready = false;
                Running = false;

                if (_keepAliveTimer != null)
                {
                    _keepAliveTimer.Dispose();
                    _keepAliveTimer = null;
                }
            }
            else
            {
                Server.Logger.Log(Chraft.Logger.LogLevel.Info, "Disposing {0}", Username);
                Running = false;
                Server.RemoveClient(this);
                Server.Logger.Log(Chraft.Logger.LogLevel.Info, "Clients online: {0}", Server.Clients.Count);
                Server.FreeConnectionSlot();
            }

            _chunkSendTimer.Dispose();
            _chunkSendTimer = null;

            RecvBufferPool.ReleaseBuffer(_recvBuffer);
            SendSocketEventPool.Push(_sendSocketEvent);
            RecvSocketEventPool.Push(_recvSocketEvent);

            if (_socket.Connected)
            {
                try
                {
                    _socket.Shutdown(SocketShutdown.Both);
                }
                catch(SocketException)
                {
                    // Ignore errors in socket shutdown (e.g. if client crashes there is a no connection error when trying to shutdown)
                }
            }
            _socket.Close();
           
            //GC.Collect();
        }

        public void MarkToDispose()
        {
            lock (_disposeLock)
            {
                if (Running)
                {
                    Running = false;
                    StopUpdateChunks();
                }
            }
        }

        public void DisposeSendSystem()
        {
            lock(_disposeLock)
            {
                if (!_sendSystemDisposed)
                {
                    _sendSystemDisposed = true;
                    if (_recvSystemDisposed)
                    {
                        Server.ClientsToDispose.Enqueue(this);
                        Server.NetworkSignal.Set();
                    }
                }
            }
        }

        public void DisposeRecvSystem()
        {
            lock (_disposeLock)
            {
                if (!_recvSystemDisposed)
                {
                    _recvSystemDisposed = true;
                    if (_sendSystemDisposed)
                    {
                        Server.ClientsToDispose.Enqueue(this);
                        Server.NetworkSignal.Set();
                    }
                }
            }
        }

        /// <summary>
        /// Sends a message to the player via chat.
        /// </summary>
        /// <param name="message">The message to be displayed in the chat HUD.</param>
        public void SendMessage(string message)
        {
            SendPacket(new ChatMessagePacket
            {
                Message = message
            });
        }

        private void StartKeepAliveTimer()
        {
            _keepAliveTimer = new Timer(KeepAliveTimer_Callback, null, 10000, 10000);
        }
    }
}