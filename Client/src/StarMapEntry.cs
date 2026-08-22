using System;
using StarMap.API;
using Brutal.Logging;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Entry point that lets StarMap load the mod.</summary>
    [StarMapMod]
    public class StarMapEntry
    {
        /// <summary>Logs that the mod was loaded.</summary>
        [StarMapImmediateLoad]
        public void Init(Mod definingMod)
        {
            DefaultCategory.Log.Info("StarMap: ImmediateLoad called", "Init", nameof(StarMapEntry));
            // Initialization happens in AllModsLoaded.
        }
        
        /// <summary>Initializes the multiplayer system once all mods are loaded.</summary>
        [StarMapAllModsLoaded]
        public void AllModsLoaded()
        {
            DefaultCategory.Log.Info("StarMap: AllModsLoaded - initializing multiplayer", "AllModsLoaded", nameof(StarMapEntry));
            ModEntry.Initialize();
        }
        
        /// <summary>Updates the mod each frame after GUI rendering.</summary>
        [StarMapAfterGui]
        public void AfterGui(double dt)
        {
            ModEntry.Update(dt);
        }
        
        /// <summary>Shuts the mod down when it is unloaded.</summary>
        [StarMapUnload]
        public void Unload()
        {
            DefaultCategory.Log.Info("StarMap: Unload called", "Unload", nameof(StarMapEntry));
            ModEntry.Shutdown();
        }
    }
}
