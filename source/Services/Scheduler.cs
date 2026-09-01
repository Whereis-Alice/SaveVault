using System;
using System.Threading;
using Playnite.SDK;

namespace SaveVault.Services
{
    /// <summary>
    /// Low frequency timer behind the scheduled backup. It ticks every few minutes and lets
    /// the callback decide whether the interval has actually elapsed, so changing the
    /// interval in settings takes effect without restarting anything, and a missed tick
    /// during sleep or shutdown is caught on the next one instead of being lost.
    /// </summary>
    public class Scheduler : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static readonly TimeSpan tick = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan startupDelay = TimeSpan.FromMinutes(2);

        private readonly object gate = new object();
        private readonly Action callback;
        private Timer timer;
        private bool running;

        public Scheduler(Action callback)
        {
            this.callback = callback;
        }

        public void Start()
        {
            lock (gate)
            {
                if (timer != null)
                {
                    return;
                }

                timer = new Timer(OnTick, null, startupDelay, tick);
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (timer == null)
                {
                    return;
                }

                timer.Dispose();
                timer = null;
            }
        }

        private void OnTick(object state)
        {
            lock (gate)
            {
                // A long backup must never overlap with the next tick.
                if (running)
                {
                    return;
                }

                running = true;
            }

            try
            {
                callback();
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: scheduled backup failed.");
            }
            finally
            {
                lock (gate)
                {
                    running = false;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
