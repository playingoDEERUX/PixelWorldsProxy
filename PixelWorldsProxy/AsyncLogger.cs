using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class AsyncLogger
{
    private static ConcurrentQueue<(string, ConsoleColor?)> _queue = new ConcurrentQueue<(string, ConsoleColor?)>();
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

    public static void Log(string message, ConsoleColor? color = null)
    {
        if (string.IsNullOrEmpty(message))
            return;

        _queue.Enqueue((message, color));
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

            while (_queue.TryDequeue(out var item))
            {
                var (msg, color) = item;
                var prev = Console.ForegroundColor;
                if (color.HasValue) Console.ForegroundColor = color.Value;
                Console.Write(msg);
                Console.ForegroundColor = prev;
            }

            if (sb.Length > 0)
            {
                Console.Write(sb.ToString());
            }

            await Task.Yield();
        }
    }
}
