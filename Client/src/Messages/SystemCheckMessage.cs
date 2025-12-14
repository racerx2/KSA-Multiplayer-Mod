using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    /// <summary>
    /// Message sent by host to verify client is using the same solar system configuration.
    /// Sent immediately after a client connects.
    /// </summary>
    [MemoryPackable]
    public partial class SystemCheckMessage : GameMessage
    {
        /// <summary>
        /// The system ID from SystemLibrary.Default (e.g., "SolarSystem", "EarthMoon", "EarthOnly")
        /// </summary>
        [MemoryPackOrder(0)]
        public string HostSystemId { get; set; } = string.Empty;
        
        /// <summary>
        /// The display name of the system (e.g., "Solar System", "Earth and Moon", "Earth Only")
        /// </summary>
        [MemoryPackOrder(1)]
        public string HostSystemDisplayName { get; set; } = string.Empty;
        
        [MemoryPackConstructor]
        public SystemCheckMessage() : base((GameMessageId)NetworkPatches.MSG_ID_SYSTEM_CHECK)
        {
        }
        
        public SystemCheckMessage(string systemId, string displayName) : base((GameMessageId)NetworkPatches.MSG_ID_SYSTEM_CHECK)
        {
            HostSystemId = systemId;
            HostSystemDisplayName = displayName;
        }
        
        public override void Execute()
        {
            // Handled by NetworkPatches.DeserialisePrefix
        }
    }
}
