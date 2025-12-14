namespace KSA.Multiplayer.DedicatedServer
{
    public static class ServerConsole
    {
        private static readonly object _lock = new();
        
        public static void Info(string message)
        {
            Write(ConsoleColor.White, message);
        }
        
        public static void Success(string message)
        {
            Write(ConsoleColor.Green, message);
        }
        
        public static void Warning(string message)
        {
            Write(ConsoleColor.Yellow, message);
        }
        
        public static void Error(string message)
        {
            Write(ConsoleColor.Red, message);
        }
        
        public static void PlayerJoin(string playerName)
        {
            Write(ConsoleColor.Cyan, $"[+] {playerName} joined the game");
        }
        
        public static void PlayerLeave(string playerName)
        {
            Write(ConsoleColor.Magenta, $"[-] {playerName} left the game");
        }
        
        public static void Chat(string playerName, string message)
        {
            Write(ConsoleColor.Gray, $"[Chat] {playerName}: {message}");
        }
        
        public static void Admin(string message)
        {
            Write(ConsoleColor.Yellow, $"[Admin] {message}");
        }
        
        public static void Network(string message)
        {
            Write(ConsoleColor.DarkGray, $"[Net] {message}");
        }
        
        private static void Write(ConsoleColor color, string message)
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{timestamp}] ");
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                Console.ResetColor();
            }
        }
        
        public static void PrintStatus(int playerCount, int maxPlayers, List<string> playerNames)
        {
            lock (_lock)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("═══════════════════════════════════════");
                Console.WriteLine($"  Players Online: {playerCount}/{maxPlayers}");
                Console.WriteLine("═══════════════════════════════════════");
                
                if (playerNames.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    foreach (var name in playerNames)
                    {
                        Console.WriteLine($"  • {name}");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("  (no players)");
                }
                
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("═══════════════════════════════════════");
                Console.ResetColor();
                Console.WriteLine();
            }
        }
        
        public static void PrintHelp()
        {
            lock (_lock)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Available Commands:");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  help              - Show this help");
                Console.WriteLine("  status            - Show server status and players");
                Console.WriteLine("  list              - List connected players");
                Console.WriteLine("  say <message>     - Broadcast message to all players");
                Console.WriteLine("  kick <name>       - Kick a player");
                Console.WriteLine("  ban <name>        - Ban a player (by IP)");
                Console.WriteLine("  unban <ip>        - Unban an IP address");
                Console.WriteLine("  banlist           - Show banned IPs");
                Console.WriteLine("  stop              - Shutdown the server");
                Console.ResetColor();
                Console.WriteLine();
            }
        }
    }
}
