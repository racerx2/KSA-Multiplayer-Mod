using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable]
    public partial class VehicleRemoveMessage : GameMessage
    {
        public const byte MESSAGE_ID = 211;
        [MemoryPackOrder(0)] public string OwnerPlayerName { get; set; } = string.Empty;
        [MemoryPackOrder(1)] public string VehicleId { get; set; } = string.Empty;

        /// <summary>Stable identity of the vessel being removed.</summary>
        [MemoryPackOrder(2)] public string VesselUid { get; set; } = string.Empty;

        [MemoryPackConstructor]
        public VehicleRemoveMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
