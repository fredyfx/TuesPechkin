using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using TuesPechkin.EventHandlers;
using TuesPechkin.Util;

namespace TuesPechkin
{
    public class ThreadSafeConverter : IConverter
    {
        public IConverter InnerConverter { get; private set; }

        public ThreadSafeConverter(IConverter converter)
        {
            InnerConverter = converter;

            InnerConverter.Begin += (a, b) =>
            {
                if (this.Begin != null)
                {
                    this.Begin(this, b);
                }
            };

            InnerConverter.Error += (a, b) =>
            {
                if (this.Error != null)
                {
                    this.Error(this, b);
                }
            };

            InnerConverter.Finished += (a, b) =>
            {
                if (this.Finished != null)
                {
                    this.Finished(this, b);
                }
            };

            InnerConverter.PhaseChanged += (a, b, c) =>
            {
                if (this.PhaseChanged != null)
                {
                    this.PhaseChanged(this, b, c);
                }
            };

            InnerConverter.ProgressChanged += (a, b, c) =>
            {
                if (this.ProgressChanged != null)
                {
                    this.ProgressChanged(this, b, c);
                }
            };

            InnerConverter.Warning += (a, b) =>
            {
                if (this.Warning != null)
                {
                    this.Warning(this, b);
                }
            };

            Funnel = new Thread(Run)
            {
                IsBackground = true
            };

            Funnel.Start();
        }

        public byte[] Convert(HtmlDocument document)
        {
            throw new NotImplementedException();
        }

        public event BeginEventHandler Begin;

        public event WarningEventHandler Warning;

        public event ErrorEventHandler Error;

        public event PhaseChangedEventHandler PhaseChanged;

        public event ProgressChangedEventHandler ProgressChanged;

        public event FinishEventHandler Finished;

        private Thread Funnel { get; set; }

        private readonly object queueLock = new object();

        private readonly List<Task> taskQueue = new List<Task>();

        private TResult Invoke<TResult>(Func<TResult> @delegate)
        {
            // create the task
            var task = new Task<TResult>(@delegate);

            // we don't want the task to be completed before we start waiting for that, so the outer lock
            lock (task)
            {
                lock (queueLock)
                {
                    taskQueue.Add(task);

                    Monitor.Pulse(queueLock);
                }

                // until this point, evaluation could not start
                Monitor.Wait(task);

                if (task.Exception != null)
                {
                    throw task.Exception;
                }

                // and when we're done waiting, we know that the result was already set
                return task.Result;
            }
        }

        private void Invoke(Action @delegate)
        {
            // create the task
            var task = new Task(@delegate);

            // we don't want the task to be completed before we start waiting for that, so the outer lock
            lock (task)
            {
                lock (queueLock)
                {
                    taskQueue.Add(task);

                    Monitor.Pulse(queueLock);
                }

                // until this point, evaluation could not start
                Monitor.Wait(task);

                if (task.Exception != null)
                {
                    throw task.Exception;
                }
            }
        }

        private void Run()
        {
            try
            {
                using (WindowsIdentity.Impersonate(IntPtr.Zero))
                {
                    Thread.CurrentPrincipal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                }

                while (true)
                {
                    Task task;

                    lock (queueLock)
                    {
                        if (taskQueue.Count > 0)
                        {
                            task = taskQueue[0];
                            taskQueue.RemoveAt(0);
                        }
                        else
                        {
                            Monitor.Wait(queueLock);
                            continue;
                        }
                    }

                    // if there's a task, process it asynchronously
                    lock (task)
                    {
                        try
                        {
                            task.Action.DynamicInvoke();
                        }
                        catch (TargetInvocationException e)
                        {
                            Tracer.Critical(string.Format("Exception in SynchronizedDispatcherThread \"{0}\"", Thread.CurrentThread.Name), e);
                            task.Exception = e.InnerException;
                        }

                        // notify waiting thread about completeion
                        Monitor.Pulse(task);
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
        }

        private class Task
        {
            public Task(Action action)
            {
                this.Action = action;
            }

            public virtual Action Action { get; protected set; }

            public Exception Exception { get; set; }
        }

        private class Task<TResult> : Task
        {
            public Task(Func<TResult> @delegate) : base(null)
            {
                this.Delegate = @delegate;
                this.Action = () => this.Result = this.Delegate();
            }

            // task code
            public Func<TResult> Delegate { get; private set; }

            // result, filled out after it's executed
            public TResult Result { get; private set; }
        }
    }
}