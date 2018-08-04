﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UChainDB.Example.Chain.Core;
using UChainDB.Example.Chain.Entity;
using UChainDB.Example.Chain.Network.InMemory;
using UChainDB.Example.Chain.Network.RpcCommands;

namespace UChainDB.Example.Chain.Network
{
    public class ConnectionPool : IDisposable
    {
        internal List<ConnectionNode> nodes;
        private int magicNumber;
        private Node selfNode;
        private IPeerFactory apiClientFactory;
        private readonly IListener listener;
        private Timer reconnectTimer;
        private bool isReceiving = false;
        private Thread thReceive;
        public event EventHandler<CommandBase> OnCommandReceived;

        public ConnectionPool(Node node, int magicNumber, string[] wellKnowns, IPeerFactory apiClientFactory, IListener listener)
        {
            this.selfNode = node;
            this.magicNumber = magicNumber;
            this.nodes = wellKnowns
                .Where(_ => _ != listener.Address)
                .Select(_ => new ConnectionNode(_))
                .ToList();
            this.apiClientFactory = apiClientFactory;
            this.listener = listener;
            this.listener.OnPeerConnected += Listener_OnPeerConnected;
        }

        private void Listener_OnPeerConnected(object sender, IPeer e)
        {
            lock (this.nodes)
            {
                var prev = this.nodes.FirstOrDefault(_ => _.ApiClient.TargetAddress == e.TargetAddress && _.ApiClient.BaseAddress == e.BaseAddress);
                if (prev != null) this.nodes.Remove(prev);
                this.nodes.Add(new ConnectionNode("TODO")
                {
                    ApiClient = e,
                });
            }
        }

        public int ConnectionNumber { get; set; }

        public void Start()
        {
            this.reconnectTimer = new Timer(async (_) => await this.ConnectAllAsync(), null, new TimeSpan(0, 0, 0, 0, 100), new TimeSpan(0, 0, 20));
            this.thReceive = new Thread(Receive);
            this.thReceive.Start();
            this.isReceiving = true;
        }

        private void Receive()
        {
            try
            {
                while (this.isReceiving)
                {
                    ConnectionNode[] internalnodes;
                    lock (this.nodes)
                    {
                        internalnodes = this.nodes.ToArray();
                    }
                    foreach (var node in internalnodes)
                    {
                        if (node.ApiClient == null) continue;
                        var command = node.ApiClient.ReceiveAsync().Result;
                        if (command == null) continue;
                        OnCommandReceived?.Invoke(this, command);
                        command.OnReceived(this.selfNode, node);
                        if (!this.isReceiving) break;
                    }

                    Thread.Sleep(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"unexpected exception in receive: {ex}");
            }
        }

        private async Task ConnectAllAsync()
        {
            ConnectionNode[] internalnodes;
            lock (this.nodes)
            {
                internalnodes = this.nodes
                    .Where(_ => _.Status == ConnectionStatus.Initial || _.Status == ConnectionStatus.Dead)
                    .Where(_ => _.Address != "TODO")
                    .ToArray();
            }
            foreach (var node in internalnodes)
            {
                await this.TryConnectAsync(node);
            }
        }

        public async Task BroadcastAsync(CommandBase command)
        {
            ConnectionNode[] internalnodes;
            lock (this.nodes)
            {
                internalnodes = this.nodes
                    .Where(_ => _.Status == ConnectionStatus.Connected)
                    .ToArray();
            }
            foreach (var node in internalnodes)
            {
                await node.ApiClient.SendAsync(command);
            }
        }

        public async void Dispose()
        {
            this.isReceiving = false;
            this.reconnectTimer?.Dispose();
            ConnectionNode[] internalnodes;
            lock (this.nodes)
            {
                internalnodes = this.nodes
                    .Where(_ => _.Status == ConnectionStatus.Connected)
                    .ToArray();
            }
            foreach (var node in internalnodes)
            {
                //await node.ApiClient.CloseAsync(WebSocketCloseStatus.ProtocolError, "close", CancellationToken.None);
                await node.ApiClient.CloseAsync(CancellationToken.None);
                node.ApiClient?.Dispose();
            }
            this.thReceive.Join();
        }

        private async Task TryConnectAsync(ConnectionNode node)
        {
            // dispose previous client if exist
            if (node.ApiClient != null)
            {
                node.ApiClient.Dispose();
                node.ApiClient = null;
            }

            var client = this.apiClientFactory.Produce();
            try
            {
                await client.ConnectAsync(node.Address);
                node.ApiClient = client;
            }
            catch (Exception)
            {
                //log.LogError($"Cannot connect to server {node.Address}, due to {acex.Message}", acex);
                node.Status = ConnectionStatus.Dead;
            }

            if (!client.IsConnected)
            {
                //log.LogWarning("open api client channel failed");
                node.Status = ConnectionStatus.Dead;
                return;
            }

            try
            {
                await client.SendAsync(new VersionCommand());
            }
            catch (Exception ex)
            {
                //this.log.LogError($"something error trying connect to {node.Address}", ex);
                node.Status = ConnectionStatus.Dead;
            }
            finally
            {
                if (node.Status != ConnectionStatus.Connected)
                {
                    await client.CloseAsync();
                    client.Dispose();
                }
            }
        }
    }

    public enum ConnectionStatus
    {
        Initial,
        Self,
        DifferentNetwork,
        Connected,
        Disconnected,
        Dead,
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ", nq}")]
    public class ConnectionNode
    {
        public ConnectionNode(string address)
        {
            this.Address = address;
        }

        public string Address { get; set; }
        public ConnectionStatus Status { get; set; } = ConnectionStatus.Initial;
        public Guid NodeId { get; set; } = Guid.Empty;
        public IPeer ApiClient { get; set; }
        public BlockHead LatestBlock { get; set; }
        public ulong Height { get; set; }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        protected virtual string DebuggerDisplay => $"{this.ApiClient?.BaseAddress} -> {this.ApiClient?.TargetAddress}";
    }
}