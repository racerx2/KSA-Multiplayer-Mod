using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable]
    public partial class AuthStatusMessage : GameMessage
    {
        public const byte MESSAGE_ID = 208;
        [MemoryPackOrder(0)] public bool Success { get; set; }
        [MemoryPackOrder(1)] public string PlayerName { get; set; } = string.Empty;
        [MemoryPackOrder(2)] public string Message { get; set; } = string.Empty;

        [MemoryPackConstructor]
        public AuthStatusMessage() : base((GameMessageId)MESSAGE_ID) { }

        public override void Execute() { }
    }
}
