using System;
using System.IO;
using Tomlet;
using Tomlet.Attributes;

namespace KSA.Mods.Multiplayer
{
    [TomlDoNotInlineObject]
    public class MultiplayerSettings
    {
        private static MultiplayerSettings? _current;
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kitten Space Agency", "Mods", "Multiplayer", "settings.toml");
        
        public static MultiplayerSettings Current => _current ??= new MultiplayerSettings();
        
        [field: TomlField("defaultServerPort")]
        public ushort DefaultServerPort { get; set; } = 7777;
        
        [field: TomlField("defaultPlayerName")]
        public string DefaultPlayerName { get; set; } = "Player";
        
        [field: TomlField("maxPlayers")]
        public ushort MaxPlayers { get; set; } = 8;
        
        [field: TomlField("lastServerAddress")]
        public string LastServerAddress { get; set; } = "127.0.0.1";
        
        [field: TomlField("syncInterval")]
        public int SyncIntervalMs { get; set; } = 100;
        
        [field: TomlField("enableVesselSync")]
        public bool EnableVesselSync { get; set; } = true;
        
        [field: TomlField("enablePositionSmoothing")]
        public bool EnablePositionSmoothing { get; set; } = true;
        
        [field: TomlField("interpolationFactor")]
        public float InterpolationFactor { get; set; } = 0.1f;
        
        [field: TomlField("enableChat")]
        public bool EnableChat { get; set; } = true;
        
        [field: TomlField("chatHistorySize")]
        public int ChatHistorySize { get; set; } = 100;
        
        [field: TomlField("showJoinLeaveMessages")]
        public bool ShowJoinLeaveMessages { get; set; } = true;
        
        [field: TomlField("showNameTags")]
        public bool ShowNameTags { get; set; } = true;
        
        [field: TomlField("enableDebugLogging")]
        public bool EnableDebugLogging { get; set; } = true;
        
        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    _current = TomletMain.To<MultiplayerSettings>(File.ReadAllText(SettingsPath));
                else
                {
                    _current = new MultiplayerSettings();
                    Save();
                }
            }
            catch
            {
                _current = new MultiplayerSettings();
            }
        }
        
        public static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, TomletMain.TomlStringFrom(_current ?? new MultiplayerSettings()));
            }
            catch { }
        }
    }
}
