// 
// Copyright (c) 2004-2006 Jaroslaw Kowalski <jaak@jkowalski.net>
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

#if !NETCF

using System;
using System.Xml;
using System.IO;
using System.Threading;
using System.Collections;
using System.Collections.Specialized;

using NLog;
using NLog.Config;

using NLog.Internal;

namespace NLog.Targets.Wrappers
{
    /// <summary>
    /// The action to be taken when the queue overflows
    /// </summary>
    public enum AsyncTargetWrapperOverflowAction
    {
        /// <summary>
        /// Do no action - accept another item into the queue.
        /// </summary>
        None,

        /// <summary>
        /// Discard the overflowing item.
        /// </summary>
        Discard,

        /// <summary>
        /// Block until there's more room in the queue.
        /// </summary>
        Block,
    }

    /// <summary>
    /// A target wrapper that provides asynchronous, buffered execution of target writes.
    /// </summary>
    /// <example>
    /// <p><b>TODO - write some more whys and howtos...</b></p>
    /// <p>
    /// To set up the target in the <a href="config.html">configuration file</a>, 
    /// use the following syntax:
    /// </p>
    /// <xml src="examples/targets/AsyncWrapper/AsyncTargetWrapper.nlog" />
    /// <p>
    /// The above examples assume just one target and a single rule. See below for
    /// a programmatic configuration that's equivalent to the above config file:
    /// </p>
    /// <cs src="examples/targets/AsyncWrapper/AsyncTargetWrapper.cs" />
    /// </example>
    [Target("AsyncWrapper",IsWrapper=true)]
    [NotSupportedRuntime(Framework=RuntimeFramework.DotNetCompactFramework)]
    public class AsyncTargetWrapper: WrapperTargetBase
    {
        private int _batchSize = 100;
        private int _timeToSleepBetweenBatches = 50;

        /// <summary>
        /// Initializes the target by starting the lazy writer thread.
        /// </summary>
        public override void Initialize()
        {
            StartLazyWriterThread();
        }

        /// <summary>
        /// Closes the target by stopping the lazy writer thread.
        /// </summary>
        protected internal override void Close()
        {
            StopLazyWriterThread();
        }

        /// <summary>
        /// The number of log events that should be processed in a batch
        /// by the lazy writer thread.
        /// </summary>
        [System.ComponentModel.DefaultValue(100)]
        public int BatchSize
        {
            get { return _batchSize; }
            set { _batchSize = value; }
        }

        /// <summary>
        /// The time in milliseconds to sleep between batches.
        /// </summary>
        [System.ComponentModel.DefaultValue(50)]
        public int TimeToSleepBetweenBatches
        {
            get { return _timeToSleepBetweenBatches; }
            set { _timeToSleepBetweenBatches = value; }
        }

        private void LazyWriterThreadProc()
        {
            while (!LazyWriterThreadStopRequested)
            {
                ArrayList pendingRequests = RequestQueue.DequeueBatch(BatchSize);

                if (pendingRequests.Count == 0)
                {
                    Thread.Sleep(TimeToSleepBetweenBatches);
                    continue;
                }

                try
                {
                    LogEventInfo[] events = (LogEventInfo[])pendingRequests.ToArray(typeof(LogEventInfo));
                    WrappedTarget.Write(events);
                }
                catch (Exception ex)
                {
                    InternalLogger.Error("Error in lazy writer thread: {0}", ex);
                }
                finally
                {
                    RequestQueue.BatchProcessed(pendingRequests);
                }
            }
        }

        /// <summary>
        /// Adds the log event to asynchronous queue to be processed by 
        /// the lazy writer thread.
        /// </summary>
        /// <param name="logEvent">The log event.</param>
        /// <remarks>
        /// The <see cref="Target.PrecalculateVolatileLayouts"/> is called
        /// to ensure that the log event can be processed in another thread.
        /// </remarks>
        protected internal override void Write(LogEventInfo logEvent)
        {
            WrappedTarget.PrecalculateVolatileLayouts(logEvent);
            RequestQueue.Enqueue(logEvent);
        }

        private bool _stopLazyWriterThread = false;
        private Thread _lazyWriterThread = null;
        private AsyncRequestQueue _lazyWriterRequestQueue = new AsyncRequestQueue(10000, AsyncTargetWrapperOverflowAction.Discard);

        /// <summary>
        /// Checks whether lazy writer thread is requested to stop.
        /// </summary>
        protected bool LazyWriterThreadStopRequested
        {
            get { return _stopLazyWriterThread; }
        }

        /// <summary>
        /// The queue of lazy writer thread requests.
        /// </summary>
        protected AsyncRequestQueue RequestQueue
        {
            get { return _lazyWriterRequestQueue; }
        }

        /// <summary>
        /// The thread that is used to write log messages.
        /// </summary>
        protected Thread LazyWriterThread
        {
            get { return _lazyWriterThread; }
        }

        /// <summary>
        /// The action to be taken when the lazy writer thread request queue count
        /// exceeds the set limit.
        /// </summary>
        [System.ComponentModel.DefaultValue("Discard")]
        public AsyncTargetWrapperOverflowAction OverflowAction
        {
            get { return _lazyWriterRequestQueue.OnOverflow; }
            set { _lazyWriterRequestQueue.OnOverflow = value; }
        }

        /// <summary>
        /// The limit on the number of requests in the lazy writer thread request queue.
        /// </summary>
        [System.ComponentModel.DefaultValue(10000)]
        public int QueueLimit
        {
            get { return _lazyWriterRequestQueue.RequestLimit; }
            set { _lazyWriterRequestQueue.RequestLimit = value; }
        }

        /// <summary>
        /// Starts the lazy writer thread which periodically writes
        /// queued log messages.
        /// </summary>
        protected virtual void StartLazyWriterThread()
        {
            if (_lazyWriterThread != null)
            {
                // already started.
                return;
            }

            _stopLazyWriterThread = false;
            _lazyWriterRequestQueue.Clear();
            Internal.InternalLogger.Debug("Starting logging thread.");
            _lazyWriterThread = new Thread(new ThreadStart(LazyWriterThreadProc));
            _lazyWriterThread.IsBackground = true;
            _lazyWriterThread.Start();
        }

        /// <summary>
        /// Starts the lazy writer thread.
        /// </summary>
        protected virtual void StopLazyWriterThread()
        {
            if (_lazyWriterThread == null)
                return;

            Flush();
            _stopLazyWriterThread = true;
            if (!_lazyWriterThread.Join(3000))
            {
                InternalLogger.Warn("Logging thread failed to stop. Aborting.");
                _lazyWriterThread.Abort();
            }
            else
            {
                InternalLogger.Debug("Logging thread stopped.");
            }
            _lazyWriterRequestQueue.Clear();
        }

        /// <summary>
        /// Waits for the lazy writer thread to finish writing messages.
        /// </summary>
        public override void Flush(TimeSpan timeout)
        {
            InternalLogger.Debug("Flushing lazy writer thread. Requests in queue: {0}", _lazyWriterRequestQueue.UnprocessedRequestCount);

            DateTime deadLine = DateTime.MaxValue;
            if (timeout != TimeSpan.MaxValue)
                deadLine = DateTime.Now.Add(timeout);

            InternalLogger.Debug("Waiting until {0}", deadLine);

            while (_lazyWriterRequestQueue.UnprocessedRequestCount > 0 && DateTime.Now < deadLine)
            {
                int before = _lazyWriterRequestQueue.UnprocessedRequestCount;
                int after = before;

                // we wait max. 5 seconds, and if there's no queue activity
                // we give up
                for (int i = 0; i < 100 && DateTime.Now < deadLine; ++i)
                {
                    Thread.Sleep(50);
                    after = _lazyWriterRequestQueue.UnprocessedRequestCount;
                    if (after != before)
                        break;
                }
                if (after == before)
                {
                    InternalLogger.Debug("Aborting flush because of a possible lazy writer thread lockup. Requests in queue: {0}", _lazyWriterRequestQueue.RequestCount);
                    // some lockup - quit the thread
                    break;
                }
            }
            InternalLogger.Debug("After flush. Requests in queue: {0}", _lazyWriterRequestQueue.RequestCount);
        }

        /// <summary>
        /// Asynchronous request queue
        /// </summary>
        public class AsyncRequestQueue
        {
            private Queue _queue = new Queue();
            private int _batchedItems = 0;
            private AsyncTargetWrapperOverflowAction _overflowAction = AsyncTargetWrapperOverflowAction.Discard;
            private int _requestLimit = 10000;

            /// <summary>
            /// Creates a new instance of <see cref="AsyncRequestQueue"/> and
            /// sets the request limit and overflow action.
            /// </summary>
            /// <param name="requestLimit">Request limit.</param>
            /// <param name="overflowAction">The overflow action.</param>
            public AsyncRequestQueue(int requestLimit, AsyncTargetWrapperOverflowAction overflowAction)
            {
                _requestLimit = requestLimit;
                _overflowAction = overflowAction;
            }

            /// <summary>
            /// The request limit.
            /// </summary>
            public int RequestLimit
            {
                get { return _requestLimit; }
                set { _requestLimit = value; }
            }

            /// <summary>
            /// Action to be taken when there's no more room in
            /// the queue and another request is enqueued.
            /// </summary>
            public AsyncTargetWrapperOverflowAction OnOverflow
            {
                get { return _overflowAction; }
                set { _overflowAction = value; }
            }

            /// <summary>
            /// Enqueues another item. If the queue is overflown the appropriate
            /// action is taken as specified by <see cref="OnOverflow"/>.
            /// </summary>
            /// <param name="o">The item to be queued.</param>
            public void Enqueue(object o)
            {
                lock (this)
                {
                    if (_queue.Count >= RequestLimit)
                    {
                        switch (OnOverflow)
                        {
                            case AsyncTargetWrapperOverflowAction.Discard:
                                return;

                            case AsyncTargetWrapperOverflowAction.None:
                                break;

                            case AsyncTargetWrapperOverflowAction.Block:
                                while (_queue.Count >= RequestLimit)
                                {
                                    InternalLogger.Debug("Blocking...");
                                    if (System.Threading.Monitor.Wait(this))
                                    {
                                        InternalLogger.Debug("Entered critical section.");
                                    }
                                    else
                                    {
                                        InternalLogger.Debug("Failed to enter critical section.");
                                    }
                                }
                                InternalLogger.Debug("Limit ok.");
                                break;
                        }
                    }
                    _queue.Enqueue(o);
                }
            }

            /// <summary>
            /// Dequeues a maximum of <c>count</c> items from the queue
            /// and adds returns the <see cref="ArrayList"/> containing them.
            /// </summary>
            /// <param name="count">Maximum number of items to be dequeued.</param>
            public ArrayList DequeueBatch(int count)
            {
                ArrayList target = new ArrayList();
                lock (this)
                {
                    for (int i = 0; i < count; ++i)
                    {
                        if (_queue.Count <= 0)
                            break;

                        object o = _queue.Dequeue();

                        target.Add(o);
                    }
                    System.Threading.Monitor.PulseAll(this);
                }
                _batchedItems = target.Count;
                return target;
            }

            /// <summary>
            /// Notifies the queue that the request batch has been processed.
            /// </summary>
            /// <param name="batch">The batch.</param>
            public void BatchProcessed(ArrayList batch)
            {
                _batchedItems = 0;
            }

            /// <summary>
            /// Clears the queue.
            /// </summary>
            public void Clear()
            {
                lock (this)
                {
                    _queue.Clear();
                }
            }

            /// <summary>
            /// Number of requests currently in the queue.
            /// </summary>
            public int RequestCount
            {
                get { return _queue.Count; }
            }

            /// <summary>
            /// Number of requests currently being processed (in the queue + batched)
            /// </summary>
            public int UnprocessedRequestCount
            {
                get { return _queue.Count + _batchedItems; }
            }
        }

    }
}

#endif