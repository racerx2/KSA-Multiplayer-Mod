using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Renders nametags above player ships in 3D space.
    /// - Yellow for synced players
    /// - Red with time difference for out-of-sync (ghost) players
    /// </summary>
    public class NameTagRenderer
    {
        private readonly MultiplayerManager _multiplayerManager;
        private const string LogName = "NameTags";
        
        // Yellow color for synced nametags (RGBA)
        private static readonly byte4 SyncedColor = new byte4(255, 255, 0, 255);
        // Red color for out-of-sync (ghost) nametags
        private static readonly byte4 GhostColor = new byte4(255, 80, 80, 255);
        // Black for shadow
        private static readonly byte4 ShadowColor = new byte4(0, 0, 0, 255);
        // Vertical offset above the vehicle in screen pixels
        private const float VerticalOffset = 40f;
        
        public NameTagRenderer(MultiplayerManager multiplayerManager)
        {
            _multiplayerManager = multiplayerManager;
            Log("NameTagRenderer initialized");
        }
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        /// <summary>
        /// Called each frame to render nametags. Should be called during ImGui rendering phase.
        /// </summary>
        public void DrawNameTags()
        {
            if (!MultiplayerSettings.Current.ShowNameTags)
                return;
            
            if (!_multiplayerManager.IsConnected)
                return;
            
            // Get the main viewport and camera
            if (Program.Viewports == null || Program.Viewports.Count == 0)
                return;
            
            Viewport mainViewport = Program.MainViewport;
            if (mainViewport == null)
                return;
            
            Camera camera = mainViewport.GetCamera();
            if (camera == null)
                return;
            
            // Draw local player's ship nametag
            DrawLocalPlayerNameTag(camera);
            
            // Draw remote players' ship nametags
            DrawRemotePlayerNameTags(mainViewport, camera);
        }
        
        private void DrawLocalPlayerNameTag(Camera camera)
        {
            string? localPlayerName = _multiplayerManager.LocalPlayerName;
            if (string.IsNullOrEmpty(localPlayerName))
                return;
            
            Vehicle? localVehicle = Program.ControlledVehicle;
            if (localVehicle == null)
                return;
            
            // Get the vehicle's position relative to the camera in screen space
            double3 positionEcl = localVehicle.GetPositionEcl();
            
            // Check if behind camera first
            double3 cameraPos = camera.PositionEcl;
            double3 toVehicle = positionEcl - cameraPos;
            double3 cameraForward = camera.GetForward();
            double dot = toVehicle.X * cameraForward.X + toVehicle.Y * cameraForward.Y + toVehicle.Z * cameraForward.Z;
            if (dot < 0)
                return; // Behind camera
            
            float2 screenPos = camera.EclToScreen(positionEcl, ignoreBehind: true);
            
            // Skip if invalid screen position
            if (float.IsNaN(screenPos.X) || float.IsNaN(screenPos.Y))
                return;
            
            // Offset above the vehicle
            float2 tagPos = new float2(screenPos.X, screenPos.Y - VerticalOffset);
            
            // Draw the nametag centered above the vehicle
            DrawNameTag(tagPos, localPlayerName, SyncedColor);
        }
        
        private void DrawRemotePlayerNameTags(Viewport mainViewport, Camera camera)
        {
            if (_multiplayerManager.SyncManager == null)
                return;
            
            RemoteVehicleRenderer? renderer = _multiplayerManager.VehicleRenderer;
            if (renderer == null)
                return;
            
            SubspaceManager? subspaceManager = _multiplayerManager.SubspaceManager;
            
            var remoteVehicles = _multiplayerManager.SyncManager.GetRemoteVehicles();
            
            foreach (var kvp in remoteVehicles)
            {
                string ownerName = kvp.Value.OwnerName;
                if (string.IsNullOrEmpty(ownerName))
                    continue;
                
                // Try to get the actual vehicle object from the renderer
                Vehicle? remoteVehicle = renderer.GetRemoteVehicle(kvp.Key);
                if (remoteVehicle == null)
                    continue;
                
                // Check sync status
                bool inSync = true;
                double timeDiff = 0;
                if (subspaceManager != null)
                {
                    inSync = subspaceManager.IsInSameSubspace(ownerName);
                    timeDiff = subspaceManager.GetTimeDifference(ownerName);
                }
                
                // Check if we're in map view
                bool isMapView = mainViewport.Mode == CameraMode.Map;
                
                // If out of sync and NOT in map view, skip rendering entirely
                // (vessel is hidden, so nametag should be too)
                if (!inSync && !isMapView)
                    continue;
                
                // Build label and choose color based on sync status
                string label;
                byte4 color;
                if (inSync)
                {
                    label = ownerName;
                    color = SyncedColor;
                }
                else
                {
                    // Red ghost marker with time difference (only shown in map view)
                    string timeStr = timeDiff >= 0 ? $"+{timeDiff:F1}s" : $"{timeDiff:F1}s";
                    label = $"{ownerName} ({timeStr})";
                    color = GhostColor;
                }
                
                // Get the vehicle's position relative to the camera in screen space
                double3 positionEcl = remoteVehicle.GetPositionEcl();
                
                // Check if behind camera first
                double3 cameraPos = camera.PositionEcl;
                double3 toVehicle = positionEcl - cameraPos;
                double3 cameraForward = camera.GetForward();
                double dot = toVehicle.X * cameraForward.X + toVehicle.Y * cameraForward.Y + toVehicle.Z * cameraForward.Z;
                if (dot < 0)
                    continue; // Behind camera
                
                float2 screenPos = camera.EclToScreen(positionEcl, ignoreBehind: true);
                
                // Skip if invalid screen position
                if (float.IsNaN(screenPos.X) || float.IsNaN(screenPos.Y))
                    continue;
                
                // Offset above the vehicle
                float2 tagPos = new float2(screenPos.X, screenPos.Y - VerticalOffset);
                
                // Draw the nametag centered above the vehicle
                DrawNameTag(tagPos, label, color);
            }
        }
        
        private void DrawNameTag(float2 screenPos, string name, byte4 color)
        {
            // Calculate text size for centering
            float2 textSize = ImGui.CalcTextSize(name);
            
            // Center the text horizontally
            float2 centeredPos = new float2(screenPos.X - textSize.X / 2f, screenPos.Y);
            
            // Draw shadow first (offset by 1 pixel in both directions) for readability
            ImGui.GetBackgroundDrawList().AddText(
                centeredPos + new float2(1f, 1f), 
                ShadowColor, 
                name);
            
            // Draw the actual text
            ImGui.GetBackgroundDrawList().AddText(
                centeredPos, 
                color, 
                name);
        }
    }
}
