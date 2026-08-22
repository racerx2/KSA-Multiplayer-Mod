using System;
using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>One craft in the server's shared library.</summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class CraftLibraryEntry
    {
        /// <summary>Server-side identifier used to request this craft.</summary>
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

        /// <summary>Compressed transfer size in bytes.</summary>
        [MemoryPackOrder(5)]
        public int SizeBytes { get; set; }

        /// <summary>UTC ticks at which the craft was shared.</summary>
        [MemoryPackOrder(6)]
        public long SharedUtcTicks { get; set; }

        [MemoryPackConstructor]
        public CraftLibraryEntry() { }
    }

    /// <summary>Lists every craft the server holds.</summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class CraftLibraryMessage : GameMessage
    {
        public const byte MESSAGE_ID = 209;

        [MemoryPackOrder(0)]
        public CraftLibraryEntry[] Entries { get; set; } = Array.Empty<CraftLibraryEntry>();

        [MemoryPackConstructor]
        public CraftLibraryMessage() : base((GameMessageId)MESSAGE_ID) { }

        public CraftLibraryMessage(CraftLibraryEntry[] entries) : base((GameMessageId)MESSAGE_ID)
        {
            Entries = entries;
        }

        public override void Execute() { }
    }
}
