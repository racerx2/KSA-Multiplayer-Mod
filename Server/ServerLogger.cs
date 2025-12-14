using System;
using System.IO;

namespace KSA.Multiplayer.DedicatedServer
{
    public static class ServerLogger
    {
        private static StreamWriter? _logWriter;
        private static readonly object _lock = new();
        private static string _logPath = "";

        public static void Initialize(string logDirectory)
        {
            Directory.CreateDirectory(logDirectory);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _logPath = Path.Combine(logDirectory, $"server_{timestamp}.log");
            _logWriter = new StreamWriter(_logPath, append: false) { AutoFlush = true };
            Log("Logger initialized");
        }

        public static void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            
            lock (_lock)
            {
                _logWriter?.WriteLine(line);
            }
        }

        public static void Close()
        {
            lock (_lock)
            {
                _logWriter?.Close();
                _logWriter = null;
            }
        }
    }
}
