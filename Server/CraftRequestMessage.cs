using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Asks the server for the craft catalogue or for one craft's data.</summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class CraftRequestMessage : GameMessage
    {
        public const byte MESSAGE_ID = 210;

        /// <summary>Asks for the whole catalogue.</summary>
        public const byte REQUEST_CATALOGUE = 0;

        /// <summary>Asks for the craft named by CraftId.</summary>
        public const byte REQUEST_CRAFT = 1;

        /// <summary>REQUEST_CATALOGUE or REQUEST_CRAFT.</summary>
        [MemoryPackOrder(0)]
        public byte RequestKind { get; set; }

        /// <summary>Player making the request.</summary>
        [MemoryPackOrder(1)]
        public string RequesterPlayerName { get; set; } = string.Empty;

        /// <summary>Craft wanted, when RequestKind is REQUEST_CRAFT.</summary>
        [MemoryPackOrder(2)]
        public string CraftId { get; set; } = string.Empty;

        [MemoryPackConstructor]
        public CraftRequestMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
