namespace StarMap.API
{
    /// <summary>
    /// StarMap mod interface.
    /// Defined here to avoid requiring the StarMap.API NuGet package (requires GitHub auth).
    /// This interface must match what StarMap expects.
    /// </summary>
    public interface IStarMapMod
    {
        void OnImmediateLoad();
        void OnFullyLoaded();
        bool ImmediateUnload { get; }
        void Unload();
    }
}

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// StarMap mod loader entry point.
    /// This allows the mod to be loaded by StarMap in addition to native KSA loading.
    /// </summary>
    public class Multiplayer : StarMap.API.IStarMapMod
    {
        /// <summary>
        /// Called immediately when the mod is loaded (before Mod.PrepareSystems).
        /// We don't do anything here - wait for full load.
        /// </summary>
        public void OnImmediateLoad()
        {
            // Nothing - wait for OnFullyLoaded
        }
        
        /// <summary>
        /// Called after all mods are loaded (after ModLibrary.LoadAll).
        /// This is where we initialize the multiplayer system.
        /// </summary>
        public void OnFullyLoaded()
        {
            ModEntry.Initialize();
        }
        
        /// <summary>
        /// Whether to call Unload immediately after OnImmediateLoad.
        /// We want to stay loaded.
        /// </summary>
        public bool ImmediateUnload => false;
        
        /// <summary>
        /// Called when the game unloads or mod is disabled.
        /// </summary>
        public void Unload()
        {
            ModEntry.Shutdown();
        }
    }
}
