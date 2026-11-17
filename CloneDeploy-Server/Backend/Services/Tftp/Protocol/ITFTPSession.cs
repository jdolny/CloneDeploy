// Adapted from JPMikkers TFTPServer. Copyright (c) 2010-2011 Jean-Paul Mikkers.
// MIT license: see LICENSE.txt.
#nullable disable
using System;
using System.Net;

namespace CodePlex.JPMikkers.TFTP
{
    internal interface ITFTPSession : IDisposable
    {
        void Start();
        void Stop();
        void ProcessAck(ushort blockNr);
        void ProcessData(ushort blockNr, ArraySegment<byte> data);
        void ProcessError(ushort code, string msg);
        IPEndPoint LocalEndPoint { get; }
        IPEndPoint RemoteEndPoint { get; }
        string Filename { get; }
    }
}
