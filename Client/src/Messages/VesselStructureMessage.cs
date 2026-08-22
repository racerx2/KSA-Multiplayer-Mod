using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    /// <summary>Announces a part-tree split or dock, addressing vessels by uid and connectors by index.</summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class VesselStructureMessage : GameMessage
    {
        public const byte MESSAGE_ID = 213;

        public const byte ACTION_SPLIT = 0;
        public const byte ACTION_DOCK = 1;

        public const byte CONNECTOR_DECOUPLER = 0;
        public const byte CONNECTOR_DOCKING_PORT = 1;

        public byte Action { get; set; }

        /// <summary>Player who performed the operation.</summary>
        public string PlayerName { get; set; } = string.Empty;

        /// <summary>SPLIT: the vessel being divided. DOCK: the vessel that absorbs the other.</summary>
        public string PrimaryUid { get; set; } = string.Empty;

        /// <summary>Index of the connector on the primary vessel.</summary>
        public int PrimaryConnectorIndex { get; set; }

        /// <summary>SPLIT only: which module list PrimaryConnectorIndex refers to.</summary>
        public byte ConnectorKind { get; set; }

        /// <summary>SPLIT only: Decoupler.Force or DockingPort.PushoffImpulse.</summary>
        public double SplitImpulse { get; set; }

        /// <summary>SPLIT only: identity the creator gave the separated vessel.</summary>
        public string NewVesselUid { get; set; } = string.Empty;

        /// <summary>DOCK only: the vessel consumed by the merge.</summary>
        public string SecondaryUid { get; set; } = string.Empty;

        /// <summary>DOCK only: index of the consumed vessel's docking port.</summary>
        public int SecondaryConnectorIndex { get; set; }

        public uint SequenceNumber { get; set; }

        /// <summary>Number of windows this replay has waited for its target's physics bubble.</summary>
        [MemoryPackIgnore]
        public int BubbleWaitFrames { get; set; }

        [MemoryPackConstructor]
        public VesselStructureMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
