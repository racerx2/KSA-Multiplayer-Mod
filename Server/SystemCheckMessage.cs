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
        
        [MemoryPackConstructor]
        public SystemCheckMessage() : base((GameMessageId)MESSAGE_ID)
        {
        }
        
        public SystemCheckMessage(string systemId, string displayName) : base((GameMessageId)MESSAGE_ID)
        {
            HostSystemId = systemId;
            HostSystemDisplayName = displayName;
        }
        
        public override void Execute()
        {
            // Server-side, not executed
        }
    }
}
