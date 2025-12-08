using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSA;
using RenderCore;
using Brutal.Numerics;
using Brutal.Logging;

namespace KSA.Mods.Multiplayer
{
    public static class VehiclePatches
    {
        private static Harmony? _harmony;
        private static readonly HashSet<string> _remoteVehicleIds = new HashSet<string>();
        private static readonly HashSet<Vehicle> _remoteVehicles = new HashSet<Vehicle>();
        private static readonly Dictionary<string, string> _vehicleOwners = new Dictionary<string, string>(); // VehicleId -> OwnerPlayerName
        private const string LogName = "Patches";
        
        // Reference to SubspaceManager for visibility checks
        private static SubspaceManager? _subspaceManager;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public static int RemoteVehicleCount => _remoteVehicles.Count;
        
        /// <summary>
        /// Set the SubspaceManager reference for visibility checks
        /// </summary>
        public static void SetSubspaceManager(SubspaceManager? manager)
        {
            _subspaceManager = manager;
            Log($"SubspaceManager reference set: {(manager != null ? "OK" : "null")}");
        }
        
        public static void ApplyPatches()
        {
            _harmony = new Harmony("com.ksa.mods.multiplayer.vehicle");
            
            var prepareWorkerMethod = AccessTools.Method(typeof(Vehicle), "PrepareWorker");
            if (prepareWorkerMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(PrepareWorkerPrefix));
                _harmony.Patch(prepareWorkerMethod, prefix: new HarmonyMethod(prefix));
            }
            
            var updateRenderDataMethod = AccessTools.Method(typeof(Vehicle), "UpdateRenderData");
            if (updateRenderDataMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(UpdateRenderDataPrefix));
                _harmony.Patch(updateRenderDataMethod, prefix: new HarmonyMethod(prefix));
            }
            
            var getWorldMatrixMethod = AccessTools.Method(typeof(Vehicle), "GetWorldMatrix");
            if (getWorldMatrixMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(GetWorldMatrixPrefix));
                _harmony.Patch(getWorldMatrixMethod, prefix: new HarmonyMethod(prefix));
            }
            
            // Patch the method that causes "outdated kinematic states" error for remote vehicles
            var populateAnalyticMethod = AccessTools.Method(typeof(VehicleUpdateTask), "PopulateAnalyticStatesFromKinematicStates");
            if (populateAnalyticMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(PopulateAnalyticStatesPrefix));
                _harmony.Patch(populateAnalyticMethod, prefix: new HarmonyMethod(prefix));
                Log("Patched PopulateAnalyticStatesFromKinematicStates");
            }
            else
            {
                Log("WARNING: Could not find PopulateAnalyticStatesFromKinematicStates to patch");
            }
            
            // Patch LogCategory.Error to capture console errors to our log file
            var logErrorMethod = AccessTools.Method(typeof(Brutal.Logging.LogCategory), "Error", new[] { typeof(string), typeof(string), typeof(string), typeof(int) });
            if (logErrorMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(LogCategoryErrorPrefix));
                _harmony.Patch(logErrorMethod, prefix: new HarmonyMethod(prefix));
                Log("Patched LogCategory.Error for console capture");
            }
            
            // Patch KittenRenderable constructor to bypass asset loading for remote vehicles
            var kittenRenderableCtor = AccessTools.Constructor(typeof(KittenRenderable), new[] { typeof(string) });
            if (kittenRenderableCtor != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(KittenRenderableCtorPrefix));
                _harmony.Patch(kittenRenderableCtor, prefix: new HarmonyMethod(prefix));
                Log("Patched KittenRenderable constructor for remote vehicle support");
            }
            else
            {
                Log("WARNING: Could not find KittenRenderable constructor to patch");
            }
            
            Log("Vehicle patches applied (with subspace visibility)");
        }
        
        public static void RemovePatches()
        {
            _harmony?.UnpatchAll("com.ksa.mods.multiplayer.vehicle");
            _remoteVehicleIds.Clear();
            _remoteVehicles.Clear();
            _vehicleOwners.Clear();
        }
        
        /// <summary>
        /// Register a remote vehicle with its owner for subspace tracking
        /// </summary>
        public static void RegisterRemoteVehicle(Vehicle vehicle, string ownerPlayerName)
        {
            if (vehicle == null) return;
            _remoteVehicleIds.Add(vehicle.Id);
            _remoteVehicles.Add(vehicle);
            _vehicleOwners[vehicle.Id] = ownerPlayerName;
            Log($"Registered remote vehicle: {vehicle.Id} owned by {ownerPlayerName}");
        }
        
        /// <summary>
        /// Legacy overload for backwards compatibility
        /// </summary>
        public static void RegisterRemoteVehicle(Vehicle vehicle)
        {
            if (vehicle == null) return;
            _remoteVehicleIds.Add(vehicle.Id);
            _remoteVehicles.Add(vehicle);
            // Try to extract owner from vehicle ID format: "MP_OwnerName_VehicleId"
            if (vehicle.Id.StartsWith("MP_"))
            {
                var parts = vehicle.Id.Split('_');
                if (parts.Length >= 2)
                {
                    _vehicleOwners[vehicle.Id] = parts[1];
                }
            }
            Log($"Registered remote vehicle: {vehicle.Id}");
        }
        
        public static void UnregisterRemoteVehicle(Vehicle vehicle)
        {
            if (vehicle == null) return;
            _remoteVehicleIds.Remove(vehicle.Id);
            _remoteVehicles.Remove(vehicle);
            _vehicleOwners.Remove(vehicle.Id);
        }
        
        public static bool IsRemoteVehicle(Vehicle vehicle)
        {
            if (vehicle == null) return false;
            return _remoteVehicles.Contains(vehicle) || _remoteVehicleIds.Contains(vehicle.Id);
        }
        
        /// <summary>
        /// Get the owner player name for a vehicle
        /// </summary>
        public static string? GetVehicleOwner(string vehicleId)
        {
            return _vehicleOwners.TryGetValue(vehicleId, out var owner) ? owner : null;
        }
        
        public static void ClearRemoteVehicles()
        {
            _remoteVehicleIds.Clear();
            _remoteVehicles.Clear();
            _vehicleOwners.Clear();
        }
        
        /// <summary>
        /// Skip physics for remote vehicles - let Kepler handle smooth motion
        /// </summary>
        public static bool PrepareWorkerPrefix(Vehicle __instance, VehicleUpdateTask updateTask)
        {
            if (IsRemoteVehicle(__instance))
            {
                // Let physics run with zero controls - no thrust, no RCS
                // This allows Kepler propagation to smoothly update position every frame
                ManualControlInputs zeroInputs = new ManualControlInputs();
                updateTask.AddVehicle(__instance, false, zeroInputs, __instance.FlightComputer);
                __instance.UpdateTask = updateTask;
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// Skip PopulateAnalyticStatesFromKinematicStates for remote vehicles.
        /// This prevents the "outdated kinematic states" error caused by us updating
        /// the orbit externally via network while physics expects to control the state.
        /// </summary>
        public static bool PopulateAnalyticStatesPrefix(object vehicleState)
        {
            try
            {
                // VehicleUpdateState is a private nested class, use reflection
                // ReadOnlyVehicle is a public field
                var vehicleField = vehicleState?.GetType().GetField("ReadOnlyVehicle");
                if (vehicleField != null)
                {
                    Vehicle? vehicle = vehicleField.GetValue(vehicleState) as Vehicle;
                    if (vehicle != null && IsRemoteVehicle(vehicle))
                    {
                        // Skip this method for remote vehicles - we update orbit via network
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }
        
        /// <summary>
        /// SUBSPACE VISIBILITY GATE
        /// Skip 3D model rendering if remote vehicle is in different subspace.
        /// The Vehicle object still exists so KSA draws its orbit on the map.
        /// </summary>
        public static bool UpdateRenderDataPrefix(Vehicle __instance, Viewport viewport, int inFrameIndex)
        {
            if (!IsRemoteVehicle(__instance))
                return true; // Local vehicle - render normally
            
            // Get owner of this remote vehicle
            string? ownerName = GetVehicleOwner(__instance.Id);
            if (string.IsNullOrEmpty(ownerName))
                return true; // Unknown owner - render to be safe
            
            // Check subspace
            if (_subspaceManager == null)
                return true; // No subspace manager - render to be safe
            
            bool sameSubspace = _subspaceManager.IsInSameSubspace(ownerName);
            
            if (!sameSubspace)
            {
                // DIFFERENT SUBSPACE = GHOST MODE
                // Skip 3D model render, but vehicle exists so orbit shows on map
                return false;
            }
            
            // SAME SUBSPACE = Full visibility
            return true;
        }
        
        /// <summary>
        /// Bypass 10km distance check for remote vehicles in same subspace
        /// </summary>
        public static bool GetWorldMatrixPrefix(Vehicle __instance, ref float4x4? __result, Camera camera)
        {
            if (!IsRemoteVehicle(__instance))
                return true;
            
            // Check subspace first
            string? ownerName = GetVehicleOwner(__instance.Id);
            if (!string.IsNullOrEmpty(ownerName) && _subspaceManager != null)
            {
                if (!_subspaceManager.IsInSameSubspace(ownerName))
                {
                    // Different subspace - return null to prevent rendering
                    __result = null;
                    return false;
                }
            }
            
            // Same subspace - bypass 10km distance check and compute full world matrix
            double3 vector = camera.GetPositionEgo(__instance);
            float4x4 translation = float4x4.CreateTranslation(new float3((float)vector.X, (float)vector.Y, (float)vector.Z));
            float4x4 rotation = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce));
            __result = rotation * translation;
            return false;
        }
        
        /// <summary>
        /// Capture KSA console errors to our log file for debugging
        /// </summary>
        public static void LogCategoryErrorPrefix(string message, string sourceMemberName, string sourceFilePath, int sourceLineNumber)
        {
            try
            {
                ModLogger.LogAlways("Console", $"[ERROR] {message} ({sourceMemberName} in {sourceFilePath}:{sourceLineNumber})");
            }
            catch { }
        }
        
        /// <summary>
        /// Patch KittenRenderable constructor to use existing CharacterAvatar for remote vehicles
        /// This avoids the asset loading issues that occur when creating new CharacterAvatars at runtime
        /// </summary>
        public static bool KittenRenderableCtorPrefix(object __instance, string characterId)
        {
            if (!RemoteVehicleRenderer._creatingRemoteKittenEva)
                return true; // Normal creation - run original constructor
            
            // Remote vehicle creation - inject existing renderable's CharacterAvatar
            if (RemoteVehicleRenderer._existingRenderableForRemote != null)
            {
                try
                {
                    Log($"KittenRenderable patch: Copying CharacterAvatar from existing renderable for '{characterId}'");
                    
                    // Get characterAvatar from existing renderable
                    var avatarField = __instance.GetType().GetField("characterAvatar", BindingFlags.NonPublic | BindingFlags.Instance);
                    var existingAvatarField = RemoteVehicleRenderer._existingRenderableForRemote.GetType().GetField("characterAvatar", BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    if (avatarField != null && existingAvatarField != null)
                    {
                        var existingAvatar = existingAvatarField.GetValue(RemoteVehicleRenderer._existingRenderableForRemote);
                        avatarField.SetValue(__instance, existingAvatar);
                        Log($"Successfully injected existing CharacterAvatar");
                        
                        // Copy other animation fields too
                        var fields = new[] { "animationIdleIndex", "timeSinceLastInput", "smoothAccel", "smoothAngleAccel", "catEarAnim", "catPersonalityExpressionAnim", "catExpressionAnim" };
                        foreach (var fieldName in fields)
                        {
                            var field = __instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                            var existingField = RemoteVehicleRenderer._existingRenderableForRemote.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                            if (field != null && existingField != null)
                            {
                                field.SetValue(__instance, existingField.GetValue(RemoteVehicleRenderer._existingRenderableForRemote));
                            }
                        }
                        
                        return false; // Skip original constructor
                    }
                }
                catch (Exception ex)
                {
                    Log($"KittenRenderable patch failed: {ex.Message}");
                }
            }
            
            Log($"KittenRenderable patch: No existing renderable available, will run original constructor");
            return true; // Run original constructor (will likely fail)
        }
    }
}
