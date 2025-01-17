﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SuperSocket.Common;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Command;
using SuperSocket.SocketBase.Logging;
using SuperSocket.SocketBase.Protocol;
using SuperSocket.SocketEngine.AsyncSocket;

namespace SuperSocket.SocketEngine
{
    class AsyncSocketSession : SocketSession, IAsyncSocketSession
    {
        private bool m_IsReset;

        private SocketAsyncEventArgs m_SocketEventArgSend;

        private int m_OrigOffset;

        public AsyncSocketSession(Socket client, SocketAsyncEventArgsProxy socketAsyncProxy)
            : this(client, socketAsyncProxy, false)
        {

        }

        public AsyncSocketSession(Socket client, SocketAsyncEventArgsProxy socketAsyncProxy, bool isReset)
            : base(client)
        {
            SocketAsyncProxy = socketAsyncProxy;
            m_OrigOffset = socketAsyncProxy.SocketEventArgs.Offset;
            m_IsReset = isReset;
        }

        ILog ILoggerProvider.Logger
        {
            get { return AppSession.Logger; }
        }

        public override void Start()
        {
            SocketAsyncProxy.Initialize(this);

            if (!SyncSend)
            {
                m_SocketEventArgSend = new SocketAsyncEventArgs();
                m_SocketEventArgSend.Completed += new EventHandler<SocketAsyncEventArgs>(OnSendingCompleted);
            }

            StartReceive(SocketAsyncProxy.SocketEventArgs);

            if (!m_IsReset)
                StartSession();
        }

        bool ProcessCompleted(SocketAsyncEventArgs e)
        {
            // check if the remote host closed the connection
            if (e.BytesTransferred <= 0)
            {
                Close(CloseReason.ClientClosing);
                return false;
            }

            if (e.SocketError != SocketError.Success)
            {
                if (Config.LogAllSocketException ||
                        (e.SocketError != SocketError.ConnectionAborted
                            && e.SocketError != SocketError.ConnectionReset
                            && e.SocketError != SocketError.Interrupted
                            && e.SocketError != SocketError.Shutdown))
                {
                    AppSession.Logger.Error(AppSession, new SocketException((int)e.SocketError));
                }

                Close(CloseReason.SocketError);
                return false;
            }

            return true;
        }

        void OnSendingCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (!ProcessCompleted(e))
                return;

            base.OnSendingCompleted();
        }

        private bool IsIgnorableException(Exception e)
        {
            if (e is ObjectDisposedException || e is NullReferenceException)
                return true;

            if (e is SocketException)
            {
                if (Config.LogAllSocketException)
                    return false;

                var se = e as SocketException;

                if (se.ErrorCode == 10004 || se.ErrorCode == 10053 || se.ErrorCode == 10054 || se.ErrorCode == 10058)
                    return true;
            }

            return false;
        }

        private void StartReceive(SocketAsyncEventArgs e)
        {
            StartReceive(e, 0);
        }

        private void StartReceive(SocketAsyncEventArgs e, int offsetDelta)
        {
            if (IsClosed)
                return;

            bool willRaiseEvent = false;

            try
            {
                if (offsetDelta < 0 || offsetDelta >= Config.ReceiveBufferSize)
                    throw new ArgumentException(string.Format("Illigal offsetDelta: {0}", offsetDelta), "offsetDelta");

                var predictOffset = m_OrigOffset + offsetDelta;

                if (e.Offset != predictOffset)
                    e.SetBuffer(predictOffset, Config.ReceiveBufferSize - offsetDelta);

                willRaiseEvent = Client.ReceiveAsync(e);
            }
            catch (Exception exc)
            {
                if (!IsIgnorableException(exc))
                    AppSession.Logger.Error(AppSession, exc);

                Close(CloseReason.SocketError);
                return;
            }

            if (!willRaiseEvent)
            {
                ProcessReceive(e);
            }
        }

        protected override void SendSync(IPosList<ArraySegment<byte>> items)
        {
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];

                    var client = Client;

                    if (client == null)
                        return;

                    client.Send(item.Array, item.Offset, item.Count, SocketFlags.None);
                }
            }
            catch (Exception e)
            {
                if (!IsIgnorableException(e))
                    AppSession.Logger.Error(AppSession, e);

                Close(CloseReason.SocketError);
                return;
            }

            OnSendingCompleted();
        }

        protected override void SendAsync(IPosList<ArraySegment<byte>> items)
        {
            try
            {
                if (items.Count > 1)
                {
                    if (m_SocketEventArgSend.Buffer != null)
                        m_SocketEventArgSend.SetBuffer(null, 0, 0);

                    m_SocketEventArgSend.BufferList = items;
                }
                else
                {
                    var currentItem = items[0];

                    try
                    {
                        if (m_SocketEventArgSend.BufferList != null)
                            m_SocketEventArgSend.BufferList = null;
                    }//Supress this exception
                    catch (Exception) //a strange NullReference exception
                    {
                        //if (AppSession.Logger.IsErrorEnabled)
                        //    AppSession.Logger.Error(AppSession, e);
                    }
                    finally
                    {
                        m_SocketEventArgSend.SetBuffer(currentItem.Array, 0, currentItem.Count);
                    }
                }

                var client = Client;

                if (client == null)
                    return;

                if (!client.SendAsync(m_SocketEventArgSend))
                    OnSendingCompleted(client, m_SocketEventArgSend);
            }
            catch (Exception e)
            {
                if (!IsIgnorableException(e))
                    AppSession.Logger.Error(AppSession, e);

                Close(CloseReason.SocketError);
            }
        }

        public SocketAsyncEventArgsProxy SocketAsyncProxy { get; private set; }

        public void ProcessReceive(SocketAsyncEventArgs e)
        {
            if (!ProcessCompleted(e))
                return;

            int offsetDelta;

            try
            {
                offsetDelta = this.AppSession.ProcessRequest(e.Buffer, e.Offset, e.BytesTransferred, true);
            }
            catch (Exception exc)
            {
                AppSession.Logger.Error(AppSession, "protocol error", exc);
                this.Close(CloseReason.ProtocolError);
                return;
            }

            //read the next block of data sent from the client
            StartReceive(e, offsetDelta);
        }      

        public override void ApplySecureProtocol()
        {
            //TODO: Implement async socket SSL/TLS encryption
        }
    }
}
