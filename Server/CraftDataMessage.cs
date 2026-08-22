using System;
using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Delivers one craft's files to the client that asked for it.</summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class CraftDataMessage : GameMessage
    {
        public const byte MESSAGE_ID = 214;

        /// <summary>Server-side identifier of the craft.</summary>
        [MemoryPackOrder(0)]
        public string CraftId { get; set; } = string.Empty;

        /// <summary>Craft name as the sharer had it.</summary>
        [MemoryPackOrder(1)]
        public string CraftName { get; set; } = string.Empty;

        /// <summary>Player who shared it.</summary>
        [MemoryPackOrder(2)]
        public string OwnerPlayerName { get; set; } = string.Empty;

        /// <summary>System id from the craft's meta.toml.</summary>
        [MemoryPackOrder(3)]
        public string SystemId { get; set; } = string.Empty;

        /// <summary>KSA version from the craft's meta.toml.</summary>
        [MemoryPackOrder(4)]
        public string GameVersion { get; set; } = string.Empty;

        /// <summary>Contents of the craft's meta.toml.</summary>
        [MemoryPackOrder(5)]
        public string MetaToml { get; set; } = string.Empty;

        /// <summary>Brotli-compressed vehicle.xml.</summary>
        [MemoryPackOrder(6)]
        public byte[] CompressedVehicleXml { get; set; } = Array.Empty<byte>();

        /// <summary>Why the craft could not be sent, or empty when it was.</summary>
        [MemoryPackOrder(7)]
        public string Error { get; set; } = string.Empty;

        [MemoryPackConstructor]
        public CraftDataMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
