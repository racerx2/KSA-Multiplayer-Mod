using System;
using MemoryPack;
using MemoryPack.Formatters;
using MemoryPack.Internal;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    public class MultiplayerChatMessage : GameMessage, IMemoryPackable<MultiplayerChatMessage>, IMemoryPackFormatterRegister
    {
        public delegate void ChatMessageDelegate(MultiplayerChatMessage message);
        public static event ChatMessageDelegate? OnChatMessageReceived;
        
        public string? SenderName;
        public string? MessageText;
        public long TimestampTicks;
        public byte MessageType;
        
        [MemoryPackConstructor]
        protected MultiplayerChatMessage() : base((GameMessageId)140) { }
        
        public MultiplayerChatMessage(string senderName, string messageText, DateTime timestamp, byte messageType)
            : base((GameMessageId)140)
        {
            SenderName = senderName;
            MessageText = messageText;
            TimestampTicks = timestamp.Ticks;
            MessageType = messageType;
        }
        
        public override void Execute() => OnChatMessageReceived?.Invoke(this);
        
        [Preserve]
        static void IMemoryPackFormatterRegister.RegisterFormatter()
        {
            if (!MemoryPackFormatterProvider.IsRegistered<MultiplayerChatMessage>())
                MemoryPackFormatterProvider.Register(new MemoryPackableFormatter<MultiplayerChatMessage>());
            if (!MemoryPackFormatterProvider.IsRegistered<MultiplayerChatMessage[]>())
                MemoryPackFormatterProvider.Register(new ArrayFormatter<MultiplayerChatMessage>());
        }
        
        [Preserve]
        static void IMemoryPackable<MultiplayerChatMessage>.Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref MultiplayerChatMessage? value)
        {
            if (value == null) { writer.WriteNullObjectHeader(); return; }
            writer.WriteObjectHeader(4);
            writer.WriteString(value.SenderName);
            writer.WriteString(value.MessageText);
            writer.WriteUnmanaged(in value.TimestampTicks);
            writer.WriteUnmanaged(in value.MessageType);
        }
        
        [Preserve]
        static void IMemoryPackable<MultiplayerChatMessage>.Deserialize(ref MemoryPackReader reader, scoped ref MultiplayerChatMessage? value)
        {
            if (!reader.TryReadObjectHeader(out var memberCount)) { value = null; return; }
            
            string? senderName = null;
            string? messageText = null;
            long timestampTicks = 0;
            byte messageType = 0;
            
            if (memberCount >= 1) senderName = reader.ReadString();
            if (memberCount >= 2) messageText = reader.ReadString();
            if (memberCount >= 3) reader.ReadUnmanaged(out timestampTicks);
            if (memberCount >= 4) reader.ReadUnmanaged(out messageType);
            
            value = new MultiplayerChatMessage
            {
                SenderName = senderName,
                MessageText = messageText,
                TimestampTicks = timestampTicks,
                MessageType = messageType
            };
        }
    }
}
