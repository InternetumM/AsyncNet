﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using AsyncNet.Core.Extensions;
using AsyncNet.Udp.Client.Events;
using AsyncNet.Udp.Error.Events;
using AsyncNet.Udp.Extensions;
using AsyncNet.Udp.Remote;
using AsyncNet.Udp.Remote.Events;

namespace AsyncNet.Udp.Client
{
    /// <summary>
    /// An implementation of asynchronous UDP client
    /// </summary>
    public class AsyncNetUdpClient : IAsyncNetUdpClient
    {
        /// <summary>
        /// Constructs UDP client that connects to the particular server and has default configuration
        /// </summary>
        /// <param name="targetHostname">Server hostname</param>
        /// <param name="targetPort">Server port</param>
        public AsyncNetUdpClient(string targetHostname, int targetPort) : this(new AsyncNetUdpClientConfig()
        {
            TargetHostname = targetHostname,
            TargetPort = targetPort
        })
        {
        }

        /// <summary>
        /// Constructs UDP client with custom configuration
        /// </summary>
        /// <param name="config">UDP client configuration</param>
        public AsyncNetUdpClient(AsyncNetUdpClientConfig config)
        {
            this.Config = new AsyncNetUdpClientConfig()
            {
                TargetHostname = config.TargetHostname,
                TargetPort = config.TargetPort,
                MaxSendQueueSize = config.MaxSendQueueSize,
                ConfigureUdpClientCallback = config.ConfigureUdpClientCallback,
                SelectIpAddressCallback = config.SelectIpAddressCallback
            };
        }

        /// <summary>
        /// Fires when client started running
        /// </summary>
        public event EventHandler<UdpClientStartedEventArgs> ClientStarted;

        /// <summary>
        /// Fires when client is ready for sending and receiving packets
        /// </summary>
        public event EventHandler<UdpClientReadyEventArgs> ClientReady;

        /// <summary>
        /// Fires when client stopped running
        /// </summary>
        public event EventHandler<UdpClientStoppedEventArgs> ClientStopped;

        /// <summary>
        /// Fires when there was a problem with the client
        /// </summary>
        public event EventHandler<UdpClientExceptionEventArgs> ClientExceptionOccured;

        /// <summary>
        /// Fires when packet arrived from server
        /// </summary>
        public event EventHandler<UdpPacketArrivedEventArgs> UdpPacketArrived;

        /// <summary>
        /// Fires when there was a problem while sending packet to the target server
        /// </summary>
        public event EventHandler<UdpSendErrorEventArgs> UdpSendErrorOccured;

        /// <summary>
        /// Underlying <see cref="UdpClient" />
        /// </summary>
        public virtual UdpClient UdpClient { get; protected set; }

        /// <summary>
        /// Asynchronously starts the client that will run indefinitely
        /// </summary>
        /// <returns><see cref="Task" /></returns>
        public virtual Task StartAsync()
        {
            return this.StartAsync(CancellationToken.None);
        }

        /// <summary>
        /// Asynchronously starts the client that will run until <paramref name="cancellationToken" /> is cancelled
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns><see cref="Task" /></returns>
        public virtual async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                this.UdpClient = this.CreateUdpClient();
            }
            catch (Exception ex)
            {
                this.OnClientExceptionOccured(new UdpClientExceptionEventArgs(ex));

                return;
            }

            this.SendQueueActionBlock = this.CreateSendQueueActionBlock(cancellationToken);
            this.CancellationToken = cancellationToken;

            this.Config.ConfigureUdpClientCallback?.Invoke(this.UdpClient);

            this.OnClientStarted(new UdpClientStartedEventArgs(this.Config.TargetHostname, this.Config.TargetPort));

            IPAddress[] addresses;

            try
            {
                addresses = await this.GetHostAddresses(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.OnClientStopped(new UdpClientStoppedEventArgs());

                this.SendQueueActionBlock.Complete();
                this.UdpClient.Dispose();

                return;
            }
            catch (Exception ex)
            {
                this.OnClientExceptionOccured(new UdpClientExceptionEventArgs(ex));

                this.OnClientStopped(new UdpClientStoppedEventArgs());

                this.SendQueueActionBlock.Complete();
                this.UdpClient.Dispose();

                return;
            }

            try
            {
                this.Connect(addresses);
            }
            catch (Exception ex)
            {
                this.OnClientExceptionOccured(new UdpClientExceptionEventArgs(ex));

                this.OnClientStopped(new UdpClientStoppedEventArgs());

                this.SendQueueActionBlock.Complete();
                this.UdpClient.Dispose();

                return;
            }

            try
            {
                await Task.WhenAll(
                    this.ReceiveAsync(cancellationToken),
                    Task.Run(() => this.OnClientReady(new UdpClientReadyEventArgs(this))))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.OnClientExceptionOccured(new UdpClientExceptionEventArgs(ex));

                return;
            }
            finally
            {
                this.OnClientStopped(new UdpClientStoppedEventArgs());

                this.SendQueueActionBlock.Complete();
                this.UdpClient.Dispose();
            }
        }

        /// <summary>
        /// Adds data to the send queue. It will fail if send queue buffer is full returning false
        /// </summary>
        /// <param name="data">Data to send</param>
        /// <returns>True - added to the send queue. False - send queue buffer is full or client is stopped</returns>
        public virtual bool Post(byte[] data)
        {
            return this.Post(data, 0, data.Length);
        }

        /// <summary>
        /// Adds data to the send queue. It will fail if send queue buffer is full returning false
        /// </summary>
        /// <param name="buffer">Buffer containing data to send</param>
        /// <param name="offset">Data offset in <paramref name="buffer" /></param>
        /// <param name="count">Numbers of bytes to send</param>
        /// <returns>True - added to the send queue. False - send queue buffer is full or client is stopped</returns>
        public virtual bool Post(byte[] buffer, int offset, int count)
        {
            return this.SendQueueActionBlock.Post(new UdpOutgoingPacket(this.TargetEndPoint, new Core.AsyncNetBuffer(buffer, offset, count), CancellationToken.None));
        }

        /// <summary>
        /// Adds data to the send queue. It will wait asynchronously if the send queue buffer is full
        /// </summary>
        /// <param name="data">Data to send</param>
        /// <returns>True - added to the send queue. False - client is stopped</returns>
        public virtual Task<bool> AddToSendQueueAsync(byte[] data)
        {
            return this.AddToSendQueueAsync(data, 0, data.Length);
        }

        /// <summary>
        /// Adds data to the send queue. It will wait asynchronously if the send queue buffer is full
        /// </summary>
        /// <param name="buffer">Buffer containing data to send</param>
        /// <param name="offset">Data offset in <paramref name="buffer" /></param>
        /// <param name="count">Numbers of bytes to send</param>
        /// <returns>True - added to the send queue. False - client is stopped</returns>
        public virtual Task<bool> AddToSendQueueAsync(byte[] buffer, int offset, int count)
        {
            return this.AddToSendQueueAsync(buffer, offset, count, CancellationToken.None);
        }

        /// <summary>
        /// Adds data to the send queue. It will wait asynchronously if the send queue buffer is full
        /// </summary>
        /// <param name="data">Data to send</param>
        /// <param name="cancellationToken">Cancellation token for cancelling this operation</param>
        /// <returns>True - added to the send queue. False - client is stopped</returns>
        public Task<bool> AddToSendQueueAsync(byte[] data, CancellationToken cancellationToken)
        {
            return this.AddToSendQueueAsync(data, 0, data.Length, cancellationToken);
        }

        /// <summary>
        /// Adds data to the send queue. It will wait asynchronously if the send queue buffer is full
        /// </summary>
        /// <param name="buffer">Buffer containing data to send</param>
        /// <param name="offset">Data offset in <paramref name="buffer" /></param>
        /// <param name="count">Numbers of bytes to send</param>
        /// <param name="cancellationToken">Cancellation token for cancelling this operation</param>
        /// <returns>True - added to the send queue. False - client is stopped</returns>
        public async Task<bool> AddToSendQueueAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            bool result;

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(this.CancellationToken, cancellationToken))
            {
                try
                {
                    result = await this.SendQueueActionBlock.SendAsync(
                            new UdpOutgoingPacket(this.TargetEndPoint, new Core.AsyncNetBuffer(buffer, offset, count), this.CancellationToken),
                            linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (!this.CancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                    }

                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Sends data asynchronously
        /// </summary>
        /// <param name="data">Data to send</param>
        /// <returns>True - data was sent. False - client is stopped or underlying send buffer is full</returns>
        public Task<bool> SendAsync(byte[] data)
        {
            return this.SendAsync(data, 0, data.Length, CancellationToken.None);
        }

        /// <summary>
        /// Sends data asynchronously
        /// </summary>
        /// <param name="data">Data to send</param>
        /// <param name="cancellationToken">Cancellation token for cancelling this operation</param>
        /// <returns>True - data was sent. False - client is stopped or underlying send buffer is full</returns>
        public Task<bool> SendAsync(byte[] data, CancellationToken cancellationToken)
        {
            return this.SendAsync(data, 0, data.Length, cancellationToken);
        }

        /// <summary>
        /// Sends data asynchronously
        /// </summary>
        /// <param name="buffer">Buffer containing data to send</param>
        /// <param name="offset">Data offset in <paramref name="buffer" /></param>
        /// <param name="count">Numbers of bytes to send</param>
        /// <returns>True - data was sent. False - client is stopped or underlying send buffer is full</returns>
        public Task<bool> SendAsync(byte[] buffer, int offset, int count)
        {
            return this.SendAsync(buffer, offset, count, CancellationToken.None);
        }

        /// <summary>
        /// Sends data asynchronously
        /// </summary>
        /// <param name="buffer">Buffer containing data to send</param>
        /// <param name="offset">Data offset in <paramref name="buffer" /></param>
        /// <param name="count">Numbers of bytes to send</param>
        /// <param name="cancellationToken">Cancellation token for cancelling this operation</param>
        /// <returns>True - data was sent. False - client is stopped or underlying send buffer is full</returns>
        public virtual async Task<bool> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            bool result;

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(this.CancellationToken, cancellationToken))
            {
                var packet = new UdpOutgoingPacket(this.TargetEndPoint, new Core.AsyncNetBuffer(buffer, offset, count), linkedCts.Token);

                try
                {
                    result = await this.SendQueueActionBlock.SendAsync(
                        packet,
                        linkedCts.Token).ConfigureAwait(false);

                    if (!result)
                    {
                        return result;
                    }

                    using (linkedCts.Token.Register(() => packet.SendTaskCompletionSource.TrySetCanceled(linkedCts.Token)))
                    {
                        result = await packet.SendTaskCompletionSource.Task.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (!this.CancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                    }

                    result = false;
                }
            }

            return result;
        }

        protected virtual IPEndPoint TargetEndPoint { get; set; }

        protected virtual AsyncNetUdpClientConfig Config { get; set; }

        protected virtual ActionBlock<UdpOutgoingPacket> SendQueueActionBlock { get; set; }

        protected virtual CancellationToken CancellationToken { get; set; }

        protected virtual UdpClient CreateUdpClient()
        {
            return new UdpClient();
        }

        protected virtual void Connect(IPAddress[] addresses)
        {
            IPAddress selected;

            if (addresses != null || addresses.Length > 0)
            {
                if (this.Config.SelectIpAddressCallback != null)
                {
                    selected = this.Config.SelectIpAddressCallback(addresses);
                }
                else
                {
                    selected = this.SelectDefaultIpAddress(addresses);
                }

                this.UdpClient.Connect(selected, this.Config.TargetPort);

                this.TargetEndPoint = new IPEndPoint(selected, this.Config.TargetPort);
            }
            else
            {
                this.UdpClient.Connect(this.Config.TargetHostname, this.Config.TargetPort);

                this.TargetEndPoint = new IPEndPoint(IPAddress.Any, this.Config.TargetPort);
            }
        }

        protected virtual Task<IPAddress[]> GetHostAddresses(CancellationToken cancellationToken)
        {
            return DnsExtensions.GetHostAddressesWithCancellationTokenAsync(this.Config.TargetHostname, cancellationToken);
        }

        protected virtual IPAddress SelectDefaultIpAddress(IPAddress[] addresses)
        {
            return addresses[0];
        }

        protected virtual ActionBlock<UdpOutgoingPacket> CreateSendQueueActionBlock(CancellationToken token)
        {
            return new ActionBlock<UdpOutgoingPacket>(
                this.SendPacketAsync,
                new ExecutionDataflowBlockOptions()
                {
                    EnsureOrdered = true,
                    BoundedCapacity = this.Config.MaxSendQueueSize,
                    MaxDegreeOfParallelism = 1,
                    CancellationToken = token
                });
        }

        protected virtual async Task ReceiveAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await this.UdpClient.ReceiveWithCancellationTokenAsync(cancellationToken).ConfigureAwait(false);

                    this.OnUdpPacketArrived(new UdpPacketArrivedEventArgs(result.RemoteEndPoint, result.Buffer));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        protected virtual async Task SendPacketAsync(UdpOutgoingPacket packet)
        {
            int numberOfBytesSent;

            try
            {
                if (packet.Buffer.Offset == 0)
                {
                    numberOfBytesSent = await this.UdpClient.SendWithCancellationTokenAsync(packet.Buffer.Memory, packet.Buffer.Count, packet.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var bytes = packet.Buffer.ToBytes();

                    numberOfBytesSent = await this.UdpClient.SendWithCancellationTokenAsync(bytes, bytes.Length, packet.CancellationToken).ConfigureAwait(false);
                }

                if (numberOfBytesSent != packet.Buffer.Count)
                {
                    packet.SendTaskCompletionSource.TrySetResult(false);

                    this.OnUdpSendErrorOccured(new UdpSendErrorEventArgs(packet, numberOfBytesSent, null));
                }
                else
                {
                    packet.SendTaskCompletionSource.TrySetResult(true);
                }
            }
            catch (OperationCanceledException ex)
            {
                packet.SendTaskCompletionSource.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                this.OnUdpSendErrorOccured(new UdpSendErrorEventArgs(packet, 0, ex));
            }
        }

        protected virtual void OnClientStarted(UdpClientStartedEventArgs e)
        {
            this.ClientStarted?.Invoke(this, e);
        }

        protected virtual void OnClientReady(UdpClientReadyEventArgs e)
        {
            this.ClientReady?.Invoke(this, e);
        }

        protected virtual void OnClientStopped(UdpClientStoppedEventArgs e)
        {
            this.ClientStopped?.Invoke(this, e);
        }

        protected virtual void OnClientExceptionOccured(UdpClientExceptionEventArgs e)
        {
            this.ClientExceptionOccured?.Invoke(this, e);
        }

        protected virtual void OnUdpPacketArrived(UdpPacketArrivedEventArgs e)
        {
            this.UdpPacketArrived?.Invoke(this, e);
        }

        protected virtual void OnUdpSendErrorOccured(UdpSendErrorEventArgs e)
        {
            this.UdpSendErrorOccured?.Invoke(this, e);
        }
    }
}
