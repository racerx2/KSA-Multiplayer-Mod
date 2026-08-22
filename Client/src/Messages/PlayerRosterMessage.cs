using System;
using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable]
    public partial class PlayerRosterMessage : GameMessage
    {
        public const byte MESSAGE_ID = 212;
        [MemoryPackOrder(0)] public string[] PlayerNames { get; set; } = Array.Empty<string>();

        /// <summary>Name of the player hosting this server, or empty when none.</summary>
        [MemoryPackOrder(1)] public string HostName { get; set; } = string.Empty;

        [MemoryPackConstructor]
        public PlayerRosterMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
