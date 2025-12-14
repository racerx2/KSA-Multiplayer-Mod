using System;
using System.Collections.Generic;
using Brutal.Numerics;
using KSA.Networking;
using KSA.Networking.Messages;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    public class ChatManager
    {
        private readonly NetworkManager _networkManager;
        private readonly List<ChatMessage> _messageHistory;
        private bool _eventHandlersRegistered = false;
        
        public IReadOnlyList<ChatMessage> MessageHistory => _messageHistory;
        
        // Event with sender name and message text for easy UI consumption
        public event Action<string, string>? OnMessageReceived;
        
        public ChatManager(NetworkManager networkManager)
        {
            _networkManager = networkManager;
            _messageHistory = new List<ChatMessage>();
        }
        
        public void Update(double deltaTime)
        {
            if (!_eventHandlersRegistered)
            {
                NetworkPatches.OnChatMessageReceived += OnKsaChatMessageReceived;
                _eventHandlersRegistered = true;
            }
        }
        
        private void OnKsaChatMessageReceived(string message)
        {
            string senderName = "Unknown";
            string text = message;
            
            if (message.StartsWith("[") && message.Contains("]"))
            {
                int endBracket = message.IndexOf(']');
                senderName = message.Substring(1, endBracket - 1);
                text = message.Substring(endBracket + 1).TrimStart();
            }
            
            var chatMessage = new ChatMessage(senderName, text, DateTime.UtcNow, ChatMessageType.Player);
            AddMessageToHistory(chatMessage);
            OnMessageReceived?.Invoke(senderName, text);
        }
        
        /// <summary>
        /// Send a chat message (uses local player name automatically)
        /// </summary>
        public void SendMessage(string text)
        {
            if (!MultiplayerSettings.Current.EnableChat || !_networkManager.IsOnline)
                return;
            
            string senderName = MultiplayerManager.Instance?.LocalPlayerName ?? "Unknown";
            
            if (!_networkManager.IsHost && Authority.GameAuthorityId.Value == 0)
                return;
            
            var chatMessage = new ChatRequestMessage(text);
            Dispatch.ToAuthority(chatMessage);
            
            var localMessage = new ChatMessage(senderName, text, DateTime.UtcNow, ChatMessageType.Player);
            AddMessageToHistory(localMessage);
            OnMessageReceived?.Invoke(senderName, text);
        }
        
        /// <summary>
        /// Send a system message (displayed as on-screen alert)
        /// </summary>
        public void SendSystemMessage(string text)
        {
            Alert.Create(text, Color.Green, 4.0);
            // TODO: Send via network to other players
        }
        
        /// <summary>
        /// Add a local system message (displayed as on-screen alert, not in chat)
        /// </summary>
        public void AddSystemMessage(string text)
        {
            Alert.Create(text, Color.Green, 4.0);
        }
        
        private void AddMessageToHistory(ChatMessage message)
        {
            _messageHistory.Add(message);
            while (_messageHistory.Count > MultiplayerSettings.Current.ChatHistorySize)
                _messageHistory.RemoveAt(0);
        }
    }
    
    public class ChatMessage
    {
        public string SenderName { get; }
        public string Text { get; }
        public DateTime Timestamp { get; }
        public ChatMessageType Type { get; }
        
        public ChatMessage(string senderName, string text, DateTime timestamp, ChatMessageType type)
        {
            SenderName = senderName;
            Text = text;
            Timestamp = timestamp;
            Type = type;
        }
    }
    
    public enum ChatMessageType : byte
    {
        Player = 0,
        System = 1,
        Server = 2
    }
}
