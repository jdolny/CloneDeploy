// Adapted from JPMikkers TFTPServer. Copyright (c) 2010-2011 Jean-Paul Mikkers.
// MIT license: see LICENSE.txt.
#nullable disable
using System;
using System.Net;

namespace CodePlex.JPMikkers.TFTP
{
    public class SessionLogEntry
    {
        public class TConfiguration
        {
            public DateTime StartTime;
            public bool IsUpload;
            public string Filename;
            public long FileLength;
            public IPEndPoint LocalEndPoint;
            public IPEndPoint RemoteEndPoint;
            public int WindowSize;
        }

        public enum TState
        {
            Busy,
            Stopped,
            Completed,
            Zombie
        }

        public long Id;
        public TConfiguration Configuration;
        public TState State;
        public long Transferred;
        public double Speed;
        public Exception Exception;
    }
}
