// Adapted from JPMikkers TFTPServer. Copyright (c) 2010-2011 Jean-Paul Mikkers.
// MIT license: see LICENSE.txt.
#nullable disable
using System;
using System.Threading;

namespace CodePlex.JPMikkers.TFTP
{
    public class DelayedDisposer
    {
        private readonly Timer _timer;

        private DelayedDisposer(IDisposable obj, int timeOut)
        {
            _timer = new Timer(x => { obj.Dispose(); _timer.Dispose(); });
            _timer.Change(timeOut, Timeout.Infinite);
        }

        public static void QueueDelayedDispose(IDisposable obj, int timeOut)
        {
            new DelayedDisposer(obj, timeOut);
        }
    }
}
