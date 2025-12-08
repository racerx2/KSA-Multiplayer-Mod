namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Central location for mod metadata - version, author, links.
    /// Update this file when releasing new versions.
    /// </summary>
    public static class ModInfo
    {
        public const string Name = "KSA Multiplayer Mod";
        public const string Version = "0.1.1";
        public const string Author = "RacerX";
        public const string GitHubUrl = "https://github.com/racerx2/KSA-Multiplayer-Mod";
        
        /// <summary>
        /// Full display string for UI: "KSA Multiplayer Mod v0.1.0"
        /// </summary>
        public static string FullName => $"{Name} v{Version}";
        
        /// <summary>
        /// Window title string: "Multiplayer v0.1.0"
        /// </summary>
        public static string WindowTitle => $"Multiplayer v{Version}";
    }
}
