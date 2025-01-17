﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SuperSocket.Common;
using SuperSocket.SocketBase.Command;
using SuperSocket.SocketBase.Config;
using SuperSocket.SocketBase.Protocol;
using SuperSocket.SocketBase.Security;

namespace SuperSocket.SocketBase
{
    /// <summary>
    /// AppServer class
    /// </summary>
    public class AppServer : AppServer<AppSession>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AppServer"/> class.
        /// </summary>
        public AppServer()
            : base()
        {

        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppServer"/> class.
        /// </summary>
        /// <param name="requestFilterFactory">The request filter factory.</param>
        public AppServer(IRequestFilterFactory<StringRequestInfo> requestFilterFactory)
            : base(requestFilterFactory)
        {

        }
    }

    /// <summary>
    /// AppServer class
    /// </summary>
    /// <typeparam name="TAppSession">The type of the app session.</typeparam>
    public class AppServer<TAppSession> : AppServer<TAppSession, StringRequestInfo>
        where TAppSession : AppSession<TAppSession, StringRequestInfo>, IAppSession, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AppServer&lt;TAppSession&gt;"/> class.
        /// </summary>
        public AppServer()
            : base(new CommandLineRequestFilterFactory())
        {

        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppServer&lt;TAppSession&gt;"/> class.
        /// </summary>
        /// <param name="requestFilterFactory">The request filter factory.</param>
        public AppServer(IRequestFilterFactory<StringRequestInfo> requestFilterFactory)
            : base(requestFilterFactory)
        {

        }
    }


    /// <summary>
    /// AppServer basic class
    /// </summary>
    /// <typeparam name="TAppSession">The type of the app session.</typeparam>
    /// <typeparam name="TRequestInfo">The type of the request info.</typeparam>
    public abstract class AppServer<TAppSession, TRequestInfo> : AppServerBase<TAppSession, TRequestInfo>, IServerStateSource
        where TRequestInfo : class, IRequestInfo
        where TAppSession : AppSession<TAppSession, TRequestInfo>, IAppSession, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AppServer&lt;TAppSession, TRequestInfo&gt;"/> class.
        /// </summary>
        public AppServer()
            : base()
        {
            
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppServer&lt;TAppSession, TRequestInfo&gt;"/> class.
        /// </summary>
        /// <param name="protocol">The protocol.</param>
        protected AppServer(IRequestFilterFactory<TRequestInfo> protocol)
            : base(protocol)
        {
   
        }

        /// <summary>
        /// Starts this AppServer instance.
        /// </summary>
        /// <returns></returns>
        public override bool Start()
        {
            if (!base.Start())
                return false;

            if (!Config.DisableSessionSnapshot)
                StartSessionSnapshotTimer();

            if (Config.ClearIdleSession)
                StartClearSessionTimer();

            return true;
        }

        private ConcurrentDictionary<string, TAppSession> m_SessionDict = new ConcurrentDictionary<string, TAppSession>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers the session into the session container.
        /// </summary>
        /// <param name="sessionID">The session ID.</param>
        /// <param name="appSession">The app session.</param>
        /// <returns></returns>
        protected override bool RegisterSession(string sessionID, TAppSession appSession)
        {
            if (m_SessionDict.TryAdd(sessionID, appSession))
                return true;

            if (Logger.IsErrorEnabled)
                Logger.Error(appSession, "The session is refused because the it's ID already exists!");

            return false;
        }

        /// <summary>
        /// Gets the app session by ID.
        /// </summary>
        /// <param name="sessionID">The session ID.</param>
        /// <returns></returns>
        public override TAppSession GetAppSessionByID(string sessionID)
        {
            if (string.IsNullOrEmpty(sessionID))
                return NullAppSession;

            TAppSession targetSession;
            m_SessionDict.TryGetValue(sessionID, out targetSession);
            return targetSession;
        }

        /// <summary>
        /// Called when [socket session closed].
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="reason">The reason.</param>
        protected override void OnSessionClosed(TAppSession session, CloseReason reason)
        {
            string sessionID = session.SessionID;

            if (!string.IsNullOrEmpty(sessionID))
            {
                TAppSession removedSession;
                if (!m_SessionDict.TryRemove(sessionID, out removedSession))
                {
                    if (Logger.IsErrorEnabled)
                        Logger.Error(session, "Failed to remove this session, Because it has't been in session container!");
                }
            }

            base.OnSessionClosed(session, reason);
        }

        /// <summary>
        /// Gets the total session count.
        /// </summary>
        public override int SessionCount
        {
            get
            {
                return m_SessionDict.Count;
            }
        }

        #region Clear idle sessions

        private System.Threading.Timer m_ClearIdleSessionTimer = null;

        private void StartClearSessionTimer()
        {
            int interval = Config.ClearIdleSessionInterval * 1000;//in milliseconds
            m_ClearIdleSessionTimer = new System.Threading.Timer(ClearIdleSession, new object(), interval, interval);
        }

        /// <summary>
        /// Clears the idle session.
        /// </summary>
        /// <param name="state">The state.</param>
        private void ClearIdleSession(object state)
        {
            if (Monitor.TryEnter(state))
            {
                try
                {
                    DateTime now = DateTime.Now;
                    DateTime timeOut = now.AddSeconds(0 - Config.IdleSessionTimeOut);

                    var timeOutSessions = SessionSource.Where(s => s.Value.LastActiveTime <= timeOut).Select(s => s.Value);
                    System.Threading.Tasks.Parallel.ForEach(timeOutSessions, s =>
                        {
                            if (Logger.IsInfoEnabled)
                                Logger.Info(s, string.Format("The session will be closed for {0} timeout, the session start time: {1}, last active time: {2}!", now.Subtract(s.LastActiveTime).TotalSeconds, s.StartTime, s.LastActiveTime));
                            s.Close(CloseReason.TimeOut);
                        });
                }
                catch (Exception e)
                {
                    if(Logger.IsErrorEnabled)
                        Logger.Error("Clear idle session error!", e);
                }
                finally
                {
                    Monitor.Exit(state);
                }
            }
        }

        private KeyValuePair<string, TAppSession>[] SessionSource
        {
            get
            {
                if (Config.DisableSessionSnapshot)
                    return m_SessionDict.ToArray();
                else
                    return m_SessionsSnapshot;
            }
        }

        #endregion

        #region Take session snapshot

        private System.Threading.Timer m_SessionSnapshotTimer = null;

        private KeyValuePair<string, TAppSession>[] m_SessionsSnapshot = new KeyValuePair<string, TAppSession>[0];

        private void StartSessionSnapshotTimer()
        {
            int interval = Math.Max(Config.SessionSnapshotInterval, 1) * 1000;//in milliseconds
            m_SessionSnapshotTimer = new System.Threading.Timer(TakeSessionSnapshot, new object(), interval, interval);
        }

        private void TakeSessionSnapshot(object state)
        {
            if (Monitor.TryEnter(state))
            {
                Interlocked.Exchange(ref m_SessionsSnapshot, m_SessionDict.ToArray());
                Monitor.Exit(state);
            }
        }

        #endregion

        #region Search session utils

        /// <summary>
        /// Gets the matched sessions from sessions snapshot.
        /// </summary>
        /// <param name="critera">The prediction critera.</param>
        /// <returns></returns>
        public override IEnumerable<TAppSession> GetSessions(Func<TAppSession, bool> critera)
        {
            return SessionSource.Select(p => p.Value).Where(critera);
        }

        /// <summary>
        /// Gets all sessions in sessions snapshot.
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<TAppSession> GetAllSessions()
        {
            return SessionSource.Select(p => p.Value);
        }

        #endregion

        #region Server state

        private ServerState m_ServerState;

        /// <summary>
        /// Gets the state of the server.
        /// </summary>
        /// <value>
        /// The state data of the server.
        /// </value>
        public override ServerState State
        {
            get { return m_ServerState; }
        }

        ServerState IServerStateSource.CollectServerState(GlobalPerformanceData globalPerfData)
        {
            m_ServerState = CollectServerState(globalPerfData);
            return m_ServerState;
        }

        private ServerState CollectServerState(GlobalPerformanceData globalPerfData)
        {
            DateTime now = DateTime.Now;

            var newServerState = CreateServerState();

            newServerState.CollectedTime = now;
            newServerState.Name = this.Name;
            newServerState.StartedTime = this.StartedTime;
            newServerState.IsRunning = this.IsRunning;
            newServerState.TotalConnections = m_SessionDict.Count;
            newServerState.MaxConnectionNumber = Config.MaxConnectionNumber;
            newServerState.TotalHandledRequests = this.TotalHandledRequests;
            newServerState.RequestHandlingSpeed = m_ServerState == null ?
                        (this.TotalHandledRequests / now.Subtract(StartedTime).TotalSeconds)
                            : ((this.TotalHandledRequests - m_ServerState.TotalHandledRequests) / now.Subtract(m_ServerState.CollectedTime).TotalSeconds);
            newServerState.Listeners = Listeners;
            //User can process the performance data by self
            this.AsyncRun(() => OnServerStateCollected(globalPerfData, newServerState), e => Logger.Error(e));

            return newServerState;
        }

        /// <summary>
        /// Creates the state of the server, you can override this method to return your own ServerState instance.
        /// </summary>
        /// <returns></returns>
        protected virtual ServerState CreateServerState()
        {
            return new ServerState();
        }

        /// <summary>
        /// Called when [performance data collected], you can override this method to get collected performance data
        /// </summary>
        /// <param name="globalPerfData">The global perf data.</param>
        /// <param name="state">The state.</param>
        protected virtual void OnServerStateCollected(GlobalPerformanceData globalPerfData, ServerState state)
        {

        }

        #endregion

        #region IDisposable Members

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {                
                if (m_SessionSnapshotTimer != null)
                {
                    m_SessionSnapshotTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    m_SessionSnapshotTimer.Dispose();
                    m_SessionSnapshotTimer = null;
                }

                if (m_ClearIdleSessionTimer != null)
                {
                    m_ClearIdleSessionTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    m_ClearIdleSessionTimer.Dispose();
                    m_ClearIdleSessionTimer = null;
                }

                var sessions = m_SessionDict.ToArray();

                if(sessions.Length > 0)
                {
                    var tasks = new Task[sessions.Length];
                    
                    for(var i = 0; i < tasks.Length; i++)
                    {
                        tasks[i] = Task.Factory.StartNew((s) =>
                            {
                                var session = s as TAppSession;

                                if (session != null)
                                {
                                    session.Close(CloseReason.ServerShutdown);
                                }

                            }, sessions[i].Value);
                    }

                    Task.WaitAll(tasks);
                }
            }
        }

        #endregion
    }
}
