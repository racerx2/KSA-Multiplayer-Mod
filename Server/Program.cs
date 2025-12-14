using KSA.Multiplayer.DedicatedServer;

Console.WriteLine("========================================");
Console.WriteLine("  KSA Multiplayer Dedicated Server");
Console.WriteLine("========================================");
Console.WriteLine();

// Load config from file
var config = ServerConfig.Load();

string logDir = Path.Combine(AppContext.BaseDirectory, "logs");

// Parse command line args (override config values)
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

// Save config (persists any command line overrides)
config.Save();

// Initialize logging
ServerLogger.Initialize(logDir);

Console.WriteLine($"Server: {config.ServerName}");
Console.WriteLine($"Port: {config.Port}");
Console.WriteLine($"Max Players: {config.MaxPlayers}");
Console.WriteLine($"System: {config.SystemId} ({config.SystemDisplayName})");
Console.WriteLine($"Logging to: {logDir}");
Console.WriteLine();

using var server = new DedicatedServer(config);

// Handle Ctrl+C
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    server.Stop();
    ServerLogger.Close();
};

if (server.Start())
{
    server.Run();
}
else
{
    ServerLogger.Log("FATAL: Failed to start server");
    Environment.Exit(1);
}

ServerLogger.Close();
