using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable(GenerateType.Object)]
    public partial class VehicleDesignSyncMessage : GameMessage
    {
        public const byte MESSAGE_ID = 202;
        
        public string VehicleId { get; set; } = string.Empty;
        public string OwnerPlayerName { get; set; } = string.Empty;
        public string TemplateId { get; set; } = string.Empty;
        public uint SequenceNumber { get; set; }
        
        [MemoryPackConstructor]
        public VehicleDesignSyncMessage() : base((GameMessageId)MESSAGE_ID) { }
        
        public override void Execute() { }
    }
}
