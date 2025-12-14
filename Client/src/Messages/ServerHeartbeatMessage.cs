using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable(GenerateType.Object)]
    public partial class ServerHeartbeatMessage : GameMessage
    {
        public const byte MESSAGE_ID = 205;
        
        /// <summary>
        /// Authoritative server time in seconds.
        /// All clients should sync to this time.
        /// </summary>
        [MemoryPackOrder(0)]
        public double ServerTimeSeconds { get; set; }
        
        [MemoryPackConstructor]
        public ServerHeartbeatMessage() : base((GameMessageId)MESSAGE_ID) { }
        
        public ServerHeartbeatMessage(double serverTimeSeconds) : base((GameMessageId)MESSAGE_ID)
        {
            ServerTimeSeconds = serverTimeSeconds;
        }
        
        public override void Execute() { }
    }
}
