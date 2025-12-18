using System;
using StarMap.API;
using Brutal.Logging;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// StarMap mod loader entry point.
    /// This allows the mod to be loaded by StarMap in addition to native KSA loading.
    /// Uses attribute-based lifecycle hooks as per StarMap API.
    /// </summary>
    [StarMapMod]
    public class StarMapEntry
    {
        /// <summary>
        /// Called immediately when the mod is loaded.
        /// </summary>
        [StarMapImmediateLoad]
        public void Init(Mod definingMod)
        {
            DefaultCategory.Log.Info("StarMap: ImmediateLoad called", "Init", nameof(StarMapEntry));
            // Don't initialize yet - wait for AllModsLoaded
        }
        
        /// <summary>
        /// Called after all mods are loaded.
        /// This is where we initialize the multiplayer system.
        /// </summary>
        [StarMapAllModsLoaded]
        public void AllModsLoaded()
        {
            DefaultCategory.Log.Info("StarMap: AllModsLoaded - initializing multiplayer", "AllModsLoaded", nameof(StarMapEntry));
            ModEntry.Initialize();
        }
        
        /// <summary>
        /// Called every frame after GUI rendering.
        /// </summary>
        [StarMapAfterGui]
        public void AfterGui(double dt)
        {
            ModEntry.Update(dt);
        }
        
        /// <summary>
        /// Called when the mod is unloaded.
        /// </summary>
        [StarMapUnload]
        public void Unload()
        {
            DefaultCategory.Log.Info("StarMap: Unload called", "Unload", nameof(StarMapEntry));
            ModEntry.Shutdown();
        }
    }
}
