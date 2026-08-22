using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    [MemoryPackable]
    public partial class SystemCheckMessage : GameMessage
    {
        public const byte MESSAGE_ID = 204;
        
        [MemoryPackOrder(0)]
        public string HostSystemId { get; set; } = string.Empty;
        
        [MemoryPackOrder(1)]
        public string HostSystemDisplayName { get; set; } = string.Empty;
        
        /// <summary>The host's mod version, for comparison against the client's.</summary>
        [MemoryPackOrder(2)]
        public string HostModVersion { get; set; } = string.Empty;
        
        /// <summary>The game type the host expects: "Sandbox" or "Testing".</summary>
        [MemoryPackOrder(3)]
        public string HostGameType { get; set; } = string.Empty;
        
        [MemoryPackConstructor]
        public SystemCheckMessage() : base((GameMessageId)MESSAGE_ID)
        {
        }
        
        public SystemCheckMessage(string systemId, string displayName, string modVersion = "",
                                  string gameType = "")
            : base((GameMessageId)MESSAGE_ID)
        {
            HostSystemId = systemId;
            HostSystemDisplayName = displayName;
            HostModVersion = modVersion;
            HostGameType = gameType;
        }
        
        public override void Execute()
        {
            // Does nothing on the server.
        }
    }
}
