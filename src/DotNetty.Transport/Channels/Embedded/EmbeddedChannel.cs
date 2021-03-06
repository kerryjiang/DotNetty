﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Channels.Embedded
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Threading.Tasks;
    using DotNetty.Common;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Common.Utilities;

    public class EmbeddedChannel : AbstractChannel
    {
        static readonly EndPoint LOCAL_ADDRESS = new EmbeddedSocketAddress();
        static readonly EndPoint REMOTE_ADDRESS = new EmbeddedSocketAddress();

        enum State
        {
            Open,
            Active,
            Closed
        };

        static readonly IChannelHandler[] EMPTY_HANDLERS = new IChannelHandler[0];

        //TODO: ChannelMetadata
        static readonly IInternalLogger logger = InternalLoggerFactory.GetInstance<EmbeddedChannel>();

        readonly EmbeddedEventLoop loop = new EmbeddedEventLoop();
        readonly IChannelConfiguration config;

        Queue<object> inboundMessages;
        Queue<object> outboundMessages;
        Exception lastException;
        State state;

        /// <summary>
        /// Create a new instance with an empty pipeline.
        /// </summary>
        public EmbeddedChannel()
            : this(EmbeddedChannelId.Instance, EMPTY_HANDLERS)
        {
        }

        /// <summary>
        /// Create a new instance with an empty pipeline with the specified <see cref="IChannelId"/>.
        /// </summary>
        /// <param name="channelId">The <see cref="IChannelId"/> of this channel. </param>
        public EmbeddedChannel(IChannelId channelId)
            : this(channelId, EMPTY_HANDLERS)
        {
        }

        /// <summary>
        /// Create a new instance with the pipeline initialized with the specified handlers.
        /// </summary>
        /// <param name="handlers">
        /// The <see cref="IChannelHandler"/>s that will be added to the <see cref="IChannelPipeline"/>
        /// </param>
        public EmbeddedChannel(params IChannelHandler[] handlers)
            : this(EmbeddedChannelId.Instance, handlers)
        {
        }

        /// <summary>
        /// Create a new instance with the pipeline initialized with the specified handlers.
        /// </summary>
        /// <param name="id">The <see cref="IChannelId"/> of this channel.</param>
        /// <param name="handlers">
        /// The <see cref="IChannelHandler"/>s that will be added to the <see cref="IChannelPipeline"/>
        /// </param>
        public EmbeddedChannel(IChannelId id, params IChannelHandler[] handlers)
            : base(null, id)
        {
            this.config = new DefaultChannelConfiguration(this);
            if (handlers == null)
            {
                throw new NullReferenceException("handlers cannot be null");
            }

            IChannelPipeline p = this.Pipeline;
            p.AddLast(new ActionChannelInitializer<IChannel>(channel =>
            {
                IChannelPipeline pipeline = channel.Pipeline;
                foreach (IChannelHandler h in handlers)
                {
                    if (h == null)
                    {
                        break;
                    }
                    pipeline.AddLast(h);
                }
            }));

            Task future = this.loop.RegisterAsync(this);
            Debug.Assert(future.IsCompleted);
            p.AddLast(new LastInboundHandler(this.InboundMessages, this.RecordException));
        }

        public override IChannelConfiguration Configuration
        {
            get { return this.config; }
        }

        /// <summary>
        /// Returns the <see cref="Queue{T}"/> which holds all of the <see cref="object"/>s that 
        /// were received by this <see cref="IChannel"/>.
        /// </summary>
        public Queue<object> InboundMessages
        {
            get { return this.inboundMessages ?? (this.inboundMessages = new Queue<object>()); }
        }

        /// <summary>
        /// Returns the <see cref="Queue{T}"/> which holds all of the <see cref="object"/>s that 
        /// were written by this <see cref="IChannel"/>.
        /// </summary>
        public Queue<object> OutboundMessages
        {
            get { return this.outboundMessages ?? (this.outboundMessages = new Queue<object>()); }
        }

        public T ReadInbound<T>()
        {
            return (T)Poll(this.inboundMessages);
        }

        public T ReadOutbound<T>()
        {
            return (T)Poll(this.outboundMessages);
        }

        public override bool DisconnectSupported
        {
            get { return false; }
        }

        protected override EndPoint LocalAddressInternal
        {
            get { return this.Active ? LOCAL_ADDRESS : null; }
        }

        protected override EndPoint RemoteAddressInternal
        {
            get { return this.Active ? REMOTE_ADDRESS : null; }
        }

        protected override IChannelUnsafe NewUnsafe()
        {
            return new DefaultUnsafe(this);
        }

        protected override bool IsCompatible(IEventLoop eventLoop)
        {
            return eventLoop is EmbeddedEventLoop;
        }

        protected override void DoBind(EndPoint localAddress)
        {
            //NOOP
        }

        protected override void DoRegister()
        {
            this.state = State.Active;
        }

        protected override void DoDisconnect()
        {
            this.DoClose();
        }

        protected override void DoClose()
        {
            this.state = State.Closed;
        }

        protected override void DoBeginRead()
        {
            //NOOP
        }

        protected override void DoWrite(ChannelOutboundBuffer input)
        {
            for (;;)
            {
                object msg = input.Current;
                if (msg == null)
                {
                    break;
                }

                ReferenceCountUtil.Retain(msg);
                this.OutboundMessages.Enqueue(msg);
                input.Remove();
            }
        }

        public override bool Open
        {
            get { return this.state != State.Closed; }
        }

        public override bool Active
        {
            get { return this.state == State.Active; }
        }

        /// <summary>
        /// Run all tasks (which also includes scheduled tasks) that are pending in the <see cref="IEventLoop"/>
        /// for this <see cref="IChannel"/>.
        /// </summary>
        public void RunPendingTasks()
        {
            try
            {
                this.loop.RunTasks();
            }
            catch (Exception ex)
            {
                this.RecordException(ex);
            }

            try
            {
                this.loop.RunScheduledTasks();
            }
            catch (Exception ex)
            {
                this.RecordException(ex);
            }
        }

        /// <summary>
        /// Run all pending scheduled tasks in the <see cref="IEventLoop"/> for this <see cref="IChannel"/>.
        /// </summary>
        /// <returns>
        /// The <see cref="PreciseTimeSpan"/> when the next scheduled task is ready to run. If no other task is
        /// scheduled then it will return <see cref="PreciseTimeSpan.Zero"/>.
        /// </returns>
        public PreciseTimeSpan RunScheduledPendingTasks()
        {
            try
            {
                return this.loop.RunScheduledTasks();
            }
            catch (Exception ex)
            {
                this.RecordException(ex);
                return this.loop.NextScheduledTask();
            }
        }

        void FinishPendingTasks()
        {
            this.RunPendingTasks();
            // Cancel all scheduled tasks that are left
            this.loop.CancelScheduledTasks();
        }

        /// <summary>
        /// Write messages to the inbound of this <see cref="IChannel"/>
        /// </summary>
        /// <param name="msgs">The messages to be written.</param>
        /// <returns><c>true</c> if the write operation did add something to the inbound buffer</returns>
        public bool WriteInbound(params object[] msgs)
        {
            this.EnsureOpen();
            if (msgs.Length == 0)
            {
                return IsNotEmpty(this.inboundMessages);
            }

            IChannelPipeline p = this.Pipeline;
            foreach (object m in msgs)
            {
                p.FireChannelRead(m);
            }
            p.FireChannelReadComplete();
            this.RunPendingTasks();
            this.CheckException();
            return IsNotEmpty(this.inboundMessages);
        }

        /// <summary>
        /// Write messages to the outbound of this <see cref="IChannel"/>.
        /// </summary>
        /// <param name="msgs">The messages to be written.</param>
        /// <returns><c>true</c> if the write operation did add something to the inbound buffer</returns>
        public bool WriteOutbound(params object[] msgs)
        {
            this.EnsureOpen();
            if (msgs.Length == 0)
            {
                return IsNotEmpty(this.outboundMessages);
            }

            //todo: RecyclableArrayList
            List<Task> futures = new List<Task>(msgs.Length);

            foreach (object m in msgs)
            {
                if (m == null)
                {
                    break;
                }
                futures.Add(this.WriteAsync(m));
            }

            this.Flush();

            int size = futures.Count;
            for (int i = 0; i < size; i++)
            {
                Task future = futures[i];
                Debug.Assert(future.IsCompleted);
                if (future.Exception != null)
                {
                    this.RecordException(future.Exception);
                }
            }

            this.RunPendingTasks();
            this.CheckException();
            return IsNotEmpty(this.outboundMessages);
        }

        void RecordException(Exception cause)
        {
            if (this.lastException == null)
            {
                this.lastException = cause;
            }
            else
            {
                logger.Warn(
                    "More than one exception was raised. " +
                        "Will report only the first one and log others.", cause);
            }
        }

        /// <summary>
        /// Mark this <see cref="IChannel"/> as finished. Any further try to write data to it will fail.
        /// </summary>
        /// <returns>bufferReadable returns <c>true</c></returns>
        public bool Finish()
        {
            this.CloseAsync();
            this.CheckException();
            return IsNotEmpty(this.inboundMessages) || IsNotEmpty(this.outboundMessages);
        }

        public override Task CloseAsync()
        {
            Task future = base.CloseAsync();
            this.FinishPendingTasks();
            return future;
        }

        public override Task DisconnectAsync()
        {
            Task future = base.DisconnectAsync();
            this.FinishPendingTasks();
            return future;
        }

        /// <summary>
        /// Check to see if there was any <see cref="Exception"/> and rethrow if so.
        /// </summary>
        public void CheckException()
        {
            Exception e = this.lastException;
            if (e == null)
            {
                return;
            }

            this.lastException = null;
            throw e;
        }

        /// <summary>
        /// Ensure the <see cref="IChannel"/> is open and if not throw an exception.
        /// </summary>
        protected void EnsureOpen()
        {
            if (!this.Open)
            {
                this.RecordException(new ClosedChannelException());
                this.CheckException();
            }
        }

        static bool IsNotEmpty(Queue<object> queue)
        {
            return queue != null && queue.Count > 0;
        }

        static object Poll(Queue<object> queue)
        {
            return IsNotEmpty(queue) ? queue.Dequeue() : null;
        }

        class DefaultUnsafe : AbstractUnsafe
        {
            public DefaultUnsafe(AbstractChannel channel)
                : base(channel)
            {
            }

            public override Task ConnectAsync(EndPoint remoteAddress, EndPoint localAddress)
            {
                return TaskEx.Completed;
            }
        }

        internal sealed class LastInboundHandler : ChannelHandlerAdapter
        {
            readonly Queue<object> inboundMessages;
            readonly Action<Exception> recordException;

            public LastInboundHandler(Queue<object> inboundMessages, Action<Exception> recordException)
            {
                this.inboundMessages = inboundMessages;
                this.recordException = recordException;
            }

            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                // have to pass the EmbeddedChannel.InboundMessages by reference via the constructor
                this.inboundMessages.Enqueue(message);
            }

            public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
            {
                // have to pass the EmbeddedChannel.RecordException method via reference
                this.recordException(exception);
            }
        }
    }
}