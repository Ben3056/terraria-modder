using System;
using System.Threading;
using Terraria;

namespace DebugTools
{
    /// <summary>
    /// Thin wrapper over Terraria's built-in Main.QueueMainThreadAction().
    /// Adds blocking wait capability for HTTP request handlers that need
    /// to execute code on the game thread and return results.
    /// </summary>
    public static class MainThreadDispatcher
    {
        /// <summary>
        /// Fire-and-forget: enqueue an action to run on the next game frame.
        /// </summary>
        public static void Enqueue(Action action)
        {
            Main.QueueMainThreadAction(action);
        }

        /// <summary>
        /// Enqueue an action on the main thread and block until it completes.
        /// Returns the result. Throws if the action throws or times out.
        /// </summary>
        public static T RunOnMainThread<T>(Func<T> func, int timeoutMs = 5000)
        {
            // If we're already on the main thread, just run it directly
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                return func();

            T result = default;
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                Main.QueueMainThreadAction(() =>
                {
                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });

                if (!done.Wait(timeoutMs))
                    throw new TimeoutException($"Main thread action timed out after {timeoutMs}ms");
            }

            if (error != null)
                throw error;

            return result;
        }

        /// <summary>
        /// Enqueue a void action on the main thread and block until it completes.
        /// </summary>
        public static void RunOnMainThreadAndWait(Action action, int timeoutMs = 5000)
        {
            RunOnMainThread<object>(() => { action(); return null; }, timeoutMs);
        }

        // Captured during Initialize() so we can detect if we're already on the main thread.
        private static int _mainThreadId = -1;

        /// <summary>
        /// Call from game thread during mod initialization to capture the thread ID.
        /// </summary>
        public static void Initialize()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }
    }
}
