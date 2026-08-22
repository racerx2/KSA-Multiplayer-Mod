using System;
using System.IO;
using Tomlet;
using Tomlet.Attributes;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Mod settings, persisted as TOML next to the game's own mod data.
    /// </summary>
    /// <remarks>
    /// The TOML keys are the property names verbatim, in PascalCase. Tomlet
    /// maps properties by name and offers <c>[TomlProperty]</c> to override
    /// that, but applying it here would rename every key on disk, and every
    /// existing settings.toml would then silently fall back to defaults —
    /// taking the player's name, server address and password with it. The keys
    /// are left matching the properties for that reason; rename a property only
    /// alongside a migration that rewrites the file.
    /// </remarks>
    [TomlDoNotInlineObject]
    public class MultiplayerSettings
    {
        private static MultiplayerSettings? _current;
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kitten Space Agency", "Mods", "Multiplayer", "settings.toml");
        
        public static MultiplayerSettings Current => _current ??= new MultiplayerSettings();
        
        public ushort DefaultServerPort { get; set; } = 7777;
        
        public string DefaultPlayerName { get; set; } = "Player";
        
        public string LastServerAddress { get; set; } = "ksa.example.com";
        
        public string ServerPassword { get; set; } = "";
        
        public bool EnableVesselSync { get; set; } = true;
        
        public bool EnableChat { get; set; } = true;
        
        public int ChatHistorySize { get; set; } = 100;
        
        public bool ShowJoinLeaveMessages { get; set; } = true;
        
        public bool ShowNameTags { get; set; } = true;
        
        public bool EnableDebugLogging { get; set; } = true;
        
        /// <summary>
        /// Records the docking readout every frame a pairing is inside 100 m.
        /// </summary>
        /// <remarks>
        /// Off by default. This is a per-frame trace kept for diagnosing
        /// docking alignment, and at fifteen or more lines a second it is the
        /// single largest thing the mod can write. Switch it on only while
        /// investigating a docking problem.
        /// </remarks>
        public bool LogDockingReadout { get; set; } = false;
        
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
