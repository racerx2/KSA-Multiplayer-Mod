namespace KSA.Mods.Multiplayer
{
    /// <summary>Holds the mod's name, version, author, licence and links.</summary>
    public static class ModInfo
    {
        public const string Name = "KSA Multiplayer Mod";
        public const string Version = "0.4.0";
        public const string Author = "RacerX";

        /// <summary>Copyright line shown in the About panel.</summary>
        public const string Copyright = "Copyright (c) 2025 RacerX";

        /// <summary>Licence this mod is distributed under.</summary>
        public const string License = "PolyForm Noncommercial License 1.0.0";
        public const string GitHubUrl = "https://github.com/racerx2/KSA-Multiplayer-Mod";
        
        /// <summary>Mod name with version, for UI display.</summary>
        public static string FullName => $"{Name} v{Version}";
        
        /// <summary>Short name with version, for the window title.</summary>
        public static string WindowTitle => $"Multiplayer v{Version}";
    }
}
