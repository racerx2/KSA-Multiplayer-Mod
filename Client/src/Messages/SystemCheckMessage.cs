using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    /// <summary>Carries the host's solar system configuration to a connecting client.</summary>
    [MemoryPackable]
    public partial class SystemCheckMessage : GameMessage
    {
        /// <summary>The host's system ID from SystemLibrary.Default.</summary>
        [MemoryPackOrder(0)]
        public string HostSystemId { get; set; } = string.Empty;
        
        /// <summary>The display name of the host's system.</summary>
        [MemoryPackOrder(1)]
        public string HostSystemDisplayName { get; set; } = string.Empty;
        
        /// <summary>The host's mod version, for comparison against the client's.</summary>
        [MemoryPackOrder(2)]
        public string HostModVersion { get; set; } = string.Empty;
        
        /// <summary>The game type the host expects: "Sandbox" or "Testing".</summary>
        [MemoryPackOrder(3)]
        public string HostGameType { get; set; } = string.Empty;
        
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
            // Does nothing; handled during deserialisation.
        }
    }
}
