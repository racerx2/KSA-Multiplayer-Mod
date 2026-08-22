using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    /// <summary>
    /// A docked passenger asking the owner of the merged stack to undock them,
    /// and the owner's answer.
    /// </summary>
    /// <remarks>
    /// The player who initiates a dock inherits control of the merged vessel, so
    /// on every other client that stack is a remote vessel: kept out of the
    /// physics bubbles, which is what makes KSA's Vehicle.Split refuse it, and
    /// not this client's part tree to mutate in any case. The passenger
    /// therefore cannot undock themselves — they ask, and the owner's machine
    /// performs the split, which replicates back through the ordinary
    /// VesselStructureMessage path.
    /// </remarks>
    [MemoryPackable(GenerateType.Object)]
    public partial class UndockRequestMessage : GameMessage
    {
        public const byte MESSAGE_ID = 215;

        /// <summary>The passenger is asking to be let off.</summary>
        public const byte STATUS_REQUEST = 0;

        /// <summary>The owner agreed and is performing the undock.</summary>
        public const byte STATUS_ACCEPTED = 1;

        /// <summary>The owner refused, or could not carry the undock out.</summary>
        public const byte STATUS_DECLINED = 2;

        /// <summary>STATUS_REQUEST, STATUS_ACCEPTED or STATUS_DECLINED.</summary>
        [MemoryPackOrder(0)]
        public byte Status { get; set; }

        /// <summary>The player who wants off the stack.</summary>
        [MemoryPackOrder(1)]
        public string RequesterPlayerName { get; set; } = string.Empty;

        /// <summary>The player whose machine owns the merged stack.</summary>
        [MemoryPackOrder(2)]
        public string OwnerPlayerName { get; set; } = string.Empty;

        /// <summary>Uid of the merged stack, as the owner names it.</summary>
        [MemoryPackOrder(3)]
        public string StackUid { get; set; } = string.Empty;

        /// <summary>CONNECTOR_DECOUPLER or CONNECTOR_DOCKING_PORT, as on VesselStructureMessage.</summary>
        [MemoryPackOrder(4)]
        public byte ConnectorKind { get; set; }

        /// <summary>Index of the connector within its module list on the stack.</summary>
        [MemoryPackOrder(5)]
        public int ConnectorIndex { get; set; }

        /// <summary>Correlates an answer with the request that asked for it.</summary>
        [MemoryPackOrder(6)]
        public uint RequestId { get; set; }

        /// <summary>Why the owner declined, for the requester to read. Empty otherwise.</summary>
        [MemoryPackOrder(7)]
        public string Reason { get; set; } = string.Empty;

        [MemoryPackConstructor]
        public UndockRequestMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
