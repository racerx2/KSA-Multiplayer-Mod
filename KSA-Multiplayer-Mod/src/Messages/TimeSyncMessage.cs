using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable(GenerateType.Object)]
    public partial class TimeSyncMessage : GameMessage
    {
        public const byte MESSAGE_ID = 201;
        
        public double SimulationTimeSeconds { get; set; }
        public double SimulationSpeed { get; set; }
        public long ServerTimestampTicks { get; set; }
        public bool IsTimeWarpActive { get; set; }
        public uint SequenceNumber { get; set; }
        
        [MemoryPackConstructor]
        public TimeSyncMessage() : base((GameMessageId)MESSAGE_ID) { }
        
        public override void Execute() { }
    }
}
