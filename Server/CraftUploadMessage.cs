using System;
using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Offers a saved craft to the server's shared library.</summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class CraftUploadMessage : GameMessage
    {
        public const byte MESSAGE_ID = 207;

        /// <summary>Player sharing the craft.</summary>
        [MemoryPackOrder(0)]
        public string OwnerPlayerName { get; set; } = string.Empty;

        /// <summary>Craft name in the sharer's vehicle list.</summary>
        [MemoryPackOrder(1)]
        public string CraftName { get; set; } = string.Empty;

        /// <summary>System id from the craft's meta.toml.</summary>
        [MemoryPackOrder(2)]
        public string SystemId { get; set; } = string.Empty;

        /// <summary>KSA version from the craft's meta.toml.</summary>
        [MemoryPackOrder(3)]
        public string GameVersion { get; set; } = string.Empty;

        /// <summary>Contents of the craft's meta.toml.</summary>
        [MemoryPackOrder(4)]
        public string MetaToml { get; set; } = string.Empty;

        /// <summary>Brotli-compressed vehicle.xml.</summary>
        [MemoryPackOrder(5)]
        public byte[] CompressedVehicleXml { get; set; } = Array.Empty<byte>();

        [MemoryPackConstructor]
        public CraftUploadMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
