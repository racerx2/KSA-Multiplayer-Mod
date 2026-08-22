using System.Runtime.CompilerServices;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Configures and runs the dedicated server.</summary>
    public static class ServerHost
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  KSA Multiplayer Dedicated Server");
            Console.WriteLine("========================================");
            Console.WriteLine();

            // Loads the config file.
            var config = ServerConfig.Load();

            string logDir = Path.Combine(AppContext.BaseDirectory, "logs");

            // Applies command line overrides to the config.
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-port" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int port))
                        config.Port = port;
                }
                else if (args[i] == "-maxplayers" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int max))
                        config.MaxPlayers = max;
                }
                else if (args[i] == "-logdir" && i + 1 < args.Length)
                    logDir = args[++i];
                else if (args[i] == "-system" && i + 1 < args.Length)
                    config.SystemId = args[++i];
                else if (args[i] == "-systemname" && i + 1 < args.Length)
                    config.SystemDisplayName = args[++i];
                else if (args[i] == "-name" && i + 1 < args.Length)
                    config.ServerName = args[++i];
            }

            // Writes the config back to disk.
            config.Save();

            // Starts the log file.
            ServerLogger.Initialize(logDir);

            Console.WriteLine($"Server: {config.ServerName}");
            Console.WriteLine($"Port: {config.Port}");
            Console.WriteLine($"Max Players: {config.MaxPlayers}");
            Console.WriteLine($"System: {config.SystemId} ({config.SystemDisplayName})");
            Console.WriteLine($"Logging to: {logDir}");
            Console.WriteLine();

            using var server = new DedicatedServer(config);

            // Asks the server to finish on Ctrl+C. It must not be stopped from this
            // thread: Run() is still calling Receive on the RakNet peer, and disposing
            // it here would free the peer underneath that loop. The `using` above
            // disposes the server once Run() has returned.
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                server.RequestShutdown();
            };

            if (server.Start())
            {
                server.Run();
            }
            else
            {
                ServerLogger.Log("FATAL: Failed to start server");
                ServerLogger.Close();
                return 1;
            }

            ServerLogger.Close();
            return 0;        }
    }
}
