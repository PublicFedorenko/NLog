// 
// Copyright (c) 2004-2017 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

namespace NLog.Internal.NetworkSenders
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Sockets;
    using NLog.Common;

    /// <summary>
    /// Sends messages over a TCP network connection.
    /// </summary>
    internal class TcpNetworkSender : NetworkSender
    {
        private readonly Queue<SocketAsyncEventArgs> _pendingRequests = new Queue<SocketAsyncEventArgs>();

        private ISocket _socket;
        private Exception _pendingError;
        private bool _asyncOperationInProgress;
        private AsyncContinuation _closeContinuation;
        private AsyncContinuation _flushContinuation;

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpNetworkSender"/> class.
        /// </summary>
        /// <param name="url">URL. Must start with tcp://.</param>
        /// <param name="addressFamily">The address family.</param>
        public TcpNetworkSender(string url, AddressFamily addressFamily)
            : base(url)
        {
            this.AddressFamily = addressFamily;
        }

        internal AddressFamily AddressFamily { get; set; }

        internal int MaxQueueSize { get; set; }

        /// <summary>
        /// Creates the socket with given parameters. 
        /// </summary>
        /// <param name="addressFamily">The address family.</param>
        /// <param name="socketType">Type of the socket.</param>
        /// <param name="protocolType">Type of the protocol.</param>
        /// <returns>Instance of <see cref="ISocket" /> which represents the socket.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "This is a factory method")]
        protected internal virtual ISocket CreateSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
        {
            return new SocketProxy(addressFamily, socketType, protocolType);
        }

        /// <summary>
        /// Performs sender-specific initialization.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Object is disposed in the event handler.")]
        protected override void DoInitialize()
        {
            var args = new MySocketAsyncEventArgs();
            args.RemoteEndPoint = this.ParseEndpointAddress(new Uri(this.Address), this.AddressFamily);
            args.Completed += this.SocketOperationCompleted;
            args.UserToken = null;

            this._socket = this.CreateSocket(args.RemoteEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            this._asyncOperationInProgress = true;

            if (!this._socket.ConnectAsync(args))
            {
                this.SocketOperationCompleted(this._socket, args);
            }
        }

        /// <summary>
        /// Closes the socket.
        /// </summary>
        /// <param name="continuation">The continuation.</param>
        protected override void DoClose(AsyncContinuation continuation)
        {
            lock (this)
            {
                if (this._asyncOperationInProgress)
                {
                    this._closeContinuation = continuation;
                }
                else
                {
                    this.CloseSocket(continuation);
                }
            }
        }

        /// <summary>
        /// Performs sender-specific flush.
        /// </summary>
        /// <param name="continuation">The continuation.</param>
        protected override void DoFlush(AsyncContinuation continuation)
        {
            lock (this)
            {
                if (!this._asyncOperationInProgress && this._pendingRequests.Count == 0)
                {
                    continuation(null);
                }
                else
                {
                    this._flushContinuation = continuation;
                }
            }
        }

        /// <summary>
        /// Sends the specified text over the connected socket.
        /// </summary>
        /// <param name="bytes">The bytes to be sent.</param>
        /// <param name="offset">Offset in buffer.</param>
        /// <param name="length">Number of bytes to send.</param>
        /// <param name="asyncContinuation">The async continuation to be invoked after the buffer has been sent.</param>
        /// <remarks>To be overridden in inheriting classes.</remarks>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Object is disposed in the event handler.")]
        protected override void DoSend(byte[] bytes, int offset, int length, AsyncContinuation asyncContinuation)
        {
            var args = new MySocketAsyncEventArgs();
            args.SetBuffer(bytes, offset, length);
            args.UserToken = asyncContinuation;
            args.Completed += this.SocketOperationCompleted;

            lock (this)
            {
                if (this.MaxQueueSize != 0 && this._pendingRequests.Count >= this.MaxQueueSize)
                {
                    var dequeued = this._pendingRequests.Dequeue();

                    if (dequeued != null)
                    {
                        dequeued.Dispose();
                    }
                }

                this._pendingRequests.Enqueue(args);
            }

            this.ProcessNextQueuedItem();
        }

        private void CloseSocket(AsyncContinuation continuation)
        {
            try
            {
                var sock = this._socket;
                this._socket = null;

                if (sock != null)
                {
                    sock.Close();
                }

                continuation(null);
            }
            catch (Exception exception)
            {
                if (exception.MustBeRethrown())
                {
                    throw;
                }

                continuation(exception);
            }
        }

        private void SocketOperationCompleted(object sender, SocketAsyncEventArgs e)
        {
            lock (this)
            {
                this._asyncOperationInProgress = false;
                var asyncContinuation = e.UserToken as AsyncContinuation;

                if (e.SocketError != SocketError.Success)
                {
                    this._pendingError = new IOException("Error: " + e.SocketError);
                }

                e.Dispose();

                if (asyncContinuation != null)
                {
                    asyncContinuation(this._pendingError);
                }
            }

            this.ProcessNextQueuedItem();
        }

        private void ProcessNextQueuedItem()
        {
            SocketAsyncEventArgs args;

            lock (this)
            {
                if (this._asyncOperationInProgress)
                {
                    return;
                }

                if (this._pendingError != null)
                {
                    while (this._pendingRequests.Count != 0)
                    {
                        args = this._pendingRequests.Dequeue();
                        var asyncContinuation = (AsyncContinuation)args.UserToken;
                        args.Dispose();
                        asyncContinuation(this._pendingError);
                    }
                }

                if (this._pendingRequests.Count == 0)
                {
                    var fc = this._flushContinuation;
                    if (fc != null)
                    {
                        this._flushContinuation = null;
                        fc(this._pendingError);
                    }

                    var cc = this._closeContinuation;
                    if (cc != null)
                    {
                        this._closeContinuation = null;
                        this.CloseSocket(cc);
                    }

                    return;
                }

                args = this._pendingRequests.Dequeue();

                this._asyncOperationInProgress = true;
                if (!this._socket.SendAsync(args))
                {
                    this.SocketOperationCompleted(this._socket, args);
                }
            }
        }

        public override void CheckSocket()
        {
            if (_socket == null)
            {
                DoInitialize();
            }
        }

        /// <summary>
        /// Facilitates mocking of <see cref="SocketAsyncEventArgs"/> class.
        /// </summary>
        internal class MySocketAsyncEventArgs : SocketAsyncEventArgs
        {
            /// <summary>
            /// Raises the Completed event.
            /// </summary>
            public void RaiseCompleted()
            {
                this.OnCompleted(this);
            }
        }
    }
}
