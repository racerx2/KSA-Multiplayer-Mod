using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    [MemoryPackable]
    public partial class PasswordAuthMessage : GameMessage
    {
        public const byte MESSAGE_ID = 206;
        
        [MemoryPackOrder(0)]
        public string Password { get; set; } = string.Empty;
        
        [MemoryPackConstructor]
        public PasswordAuthMessage() : base((GameMessageId)MESSAGE_ID)
        {
        }
        
        public override void Execute()
        {
        }
    }
}
