// Adapted from JPMikkers TFTPServer. Copyright (c) 2010-2011 Jean-Paul Mikkers.
// MIT license: see LICENSE.txt.
#nullable disable
using System;
using System.Net;

namespace CodePlex.JPMikkers.TFTP
{
    public class TFTPTraceEventArgs : EventArgs
    {
        public string Message { get; set; }
    }

    public class TFTPStopEventArgs : EventArgs
    {
        public Exception Reason { get; set; }
    }

    public interface ITFTPServer : IDisposable
    {
        event EventHandler<TFTPTraceEventArgs> OnTrace;
        event EventHandler<TFTPStopEventArgs> OnStatusChange;

        string Name { get; set; }
        IPEndPoint EndPoint { get; set; }
        bool SinglePort { get; set; }
        short Ttl { get; set; }
        bool DontFragment { get; set; }

        int ResponseTimeout { get; set; }
        int Retries { get; set; }

        string RootPath { get; set; }
        bool ConvertPathSeparator { get; set; }

        ushort WindowSize { get; set; }

        bool Active { get; }
        int ActiveTransfers { get; }

        void Start();
        void Stop();
    }
}
