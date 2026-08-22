using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSA.Multiplayer.DedicatedServer
{
    public class ServerConfig
    {
        private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "server_config.json");
        private static readonly string BanListPath = Path.Combine(AppContext.BaseDirectory, "banlist.txt");
        
        [JsonPropertyName("port")]
        public int Port { get; set; } = 7777;
        
        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; } = 8;
        
        [JsonPropertyName("systemId")]
        public string SystemId { get; set; } = "Sol";
        
        [JsonPropertyName("systemDisplayName")]
        public string SystemDisplayName { get; set; } = "Solar System";

        /// <summary>Game type clients must be running: "Sandbox" or "Testing".</summary>
        public string GameType { get; set; } = "Testing";
        
        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = "KSA Multiplayer Server";
        
        [JsonPropertyName("motd")]
        public string Motd { get; set; } = "Welcome to the server!";

        /// <summary>Name of the player hosting this server, or empty when none.</summary>
        public string HostPlayerName { get; set; } = "";
        
        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        /// <summary>Path to the local Kitten Space Agency installation, or empty to auto-detect.</summary>
        /// <remarks>
        /// Read by KsaInstall.FromConfigFile, which parses server_config.json
        /// as raw JSON rather than through this class: locating the install has
        /// to happen before the game assemblies this type depends on can be
        /// loaded. Nothing reads this property, and that is not a sign it is
        /// unused — the key must keep the name "gamePath".
        /// </remarks>
        [JsonPropertyName("gamePath")]
        public string GamePath { get; set; } = "";

        /// <summary>Whether players may share craft through this server.</summary>
        [JsonPropertyName("craftSharingEnabled")]
        public bool CraftSharingEnabled { get; set; } = true;

        /// <summary>How many craft one player may keep in the shared library.</summary>
        [JsonPropertyName("maxSharedCraftPerPlayer")]
        public int MaxSharedCraftPerPlayer { get; set; } = 32;

        [JsonIgnore]
        public HashSet<string> BannedIPs { get; private set; } = new();
        
        public static ServerConfig Load()
        {
            ServerConfig config;
            
            if (!File.Exists(ConfigPath))
            {
                config = new ServerConfig();
                config.Save();
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    config = JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading config: {ex.Message}");
                    Console.WriteLine("Using default configuration.");
                    config = new ServerConfig();
                }
            }
            
            config.LoadBanList();
            return config;
        }
        
        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
        
        private void LoadBanList()
        {
            BannedIPs.Clear();
            if (!File.Exists(BanListPath)) return;
            
            try
            {
                foreach (var line in File.ReadAllLines(BanListPath))
                {
                    var ip = line.Trim();
                    if (!string.IsNullOrEmpty(ip) && !ip.StartsWith("#"))
                        BannedIPs.Add(ip);
                }
            }
            catch { }
        }
        
        public void SaveBanList()
        {
            try
            {
                File.WriteAllLines(BanListPath, BannedIPs);
            }
            catch { }
        }
        
        public void BanIP(string ip)
        {
            BannedIPs.Add(ip);
            SaveBanList();
        }
        
        public void UnbanIP(string ip)
        {
            BannedIPs.Remove(ip);
            SaveBanList();
        }
        
        public bool IsIPBanned(string ip)
        {
            return BannedIPs.Contains(ip);
        }
    }
}
