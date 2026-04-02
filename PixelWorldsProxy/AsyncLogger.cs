using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class AsyncLogger
{
    private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private static volatile bool _running = true;

    public static void Start()
    {
        Task.Run(LoggerLoop);
    }

    public static void Stop()
    {
        _running = false;
        _signal.Set();
    }

    public static void Log(string message)
    {
        _queue.Enqueue(message);
        _signal.Set();
    }

    private static async Task LoggerLoop()
    {
        var sb = new StringBuilder();

        while (_running)
        {
            // Wait or timeout every 50ms
            _signal.WaitOne(50);

            sb.Clear();

            while (_queue.TryDequeue(out var msg))
            {
                sb.AppendLine(msg);
            }

            if (sb.Length > 0)
            {
                Console.Write(sb.ToString());
            }

            await Task.Yield();
        }
    }
}