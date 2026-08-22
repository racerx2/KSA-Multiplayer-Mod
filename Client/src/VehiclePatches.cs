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
        // A VehicleId -> OwnerPlayerName map used to be kept alongside these.
        // Nothing read it: the owner is recoverable from the vessel id itself
        // through VesselIdentity, which is where every caller went for it.
        private static readonly Dictionary<string, string> _vehicleKeys = new Dictionary<string, string>();
        
        /// <summary>Maps vehicle key to whether physics simulation is enabled.</summary>
        private static readonly Dictionary<string, bool> _physicsMode = new Dictionary<string, bool>();
        
        private const string LogName = "Patches";
        
        // Holds the SubspaceManager reference.
        private static SubspaceManager? _subspaceManager;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public static int RemoteVehicleCount => _remoteVehicles.Count;
        
        /// <summary>Sets the SubspaceManager reference.</summary>
        public static void SetSubspaceManager(SubspaceManager? manager)
        {
            _subspaceManager = manager;
            Log($"SubspaceManager reference set: {(manager != null ? "OK" : "null")}");
        }
        
        public static void ApplyPatches()
        {
            _harmony = new Harmony("com.ksa.mods.multiplayer.vehicle");
            
            // Vehicle.PrepareWorker and Vehicle.UpdateRenderData were patched
            // here with prefixes that returned true down every path, so the
            // original method ran unchanged and the only effect was Harmony's
            // dispatch on each call — both run per vehicle per frame, and
            // UpdateRenderData once more per viewport. The patches were removed
            // rather than left as placeholders; reinstate them if there is ever
            // something to do before those methods.
            
            // Keep remote vessels out of physics bubbles.
            var addToBubbleMethod = AccessTools.Method(typeof(Vehicle), "AddToBubble");
            if (addToBubbleMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(AddToBubblePrefix));
                _harmony.Patch(addToBubbleMethod, prefix: new HarmonyMethod(prefix));
                Log("Patched Vehicle.AddToBubble - remote vessels stay out of the physics loop");
            }

            var getWorldMatrixMethod = AccessTools.Method(typeof(Vehicle), "GetWorldMatrix");
            if (getWorldMatrixMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(GetWorldMatrixPrefix));
                _harmony.Patch(getWorldMatrixMethod, prefix: new HarmonyMethod(prefix));
            }

            // Patch AddVolumetricExhaustInstances to re-apply remote nozzle thrust before the plume pass.
            var exhaustMethod = AccessTools.Method(typeof(Vehicle), "AddVolumetricExhaustInstances");
            if (exhaustMethod != null)
            {
                var prefix = AccessTools.Method(typeof(VehiclePatches), nameof(AddVolumetricExhaustInstancesPrefix));
                _harmony.Patch(exhaustMethod, prefix: new HarmonyMethod(prefix));
                Log("Patched AddVolumetricExhaustInstances - remote plume state re-applied at render time");
            }
            else
            {
                Log("WARNING: AddVolumetricExhaustInstances not found - remote plumes will not render");
            }
            
            // Patch PopulateAnalyticStatesFromKinematicStates for remote vehicles.
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
            _vehicleKeys.Clear();
        }
        
        // Two more overloads stood here: one taking (vehicle, owner) that built
        // the key as "{owner}_{vehicle.Id}", and one taking the vehicle alone
        // that registered no key at all. Nothing called either. The first was
        // the more dangerous: keys are uids from VesselIdentity, separated by
        // '|' because KSA ids contain underscores, so a key it produced could
        // never be matched by GetVehicleKey's callers. Register a vehicle with
        // the key its caller already holds.
        public static void RegisterRemoteVehicle(
            Vehicle vehicle, string ownerPlayerName, string vehicleKey)
        {
            if (vehicle == null) return;
            _remoteVehicleIds.Add(vehicle.Id);
            _remoteVehicles.Add(vehicle);
            _vehicleKeys[vehicle.Id] = vehicleKey;
            Log($"Registered remote vehicle: {vehicle.Id} owned by {ownerPlayerName}");
        }
        
        public static void UnregisterRemoteVehicle(Vehicle vehicle)
        {
            if (vehicle == null) return;
            _remoteVehicleIds.Remove(vehicle.Id);
            _remoteVehicles.Remove(vehicle);
            _vehicleKeys.Remove(vehicle.Id);
        }
        
        public static bool IsRemoteVehicle(Vehicle vehicle)
        {
            if (vehicle == null) return false;
            return _remoteVehicles.Contains(vehicle) || _remoteVehicleIds.Contains(vehicle.Id);
        }

        public static void ClearRemoteVehicles()
        {
            _remoteVehicleIds.Clear();
            _remoteVehicles.Clear();
            _vehicleKeys.Clear();
            _physicsMode.Clear();
        }
        
        /// <summary>Sets the physics mode flag for a remote vehicle.</summary>
        public static void SetPhysicsMode(string vehicleKey, bool enabled)
        {
            bool previousMode = _physicsMode.GetValueOrDefault(vehicleKey, false);
            _physicsMode[vehicleKey] = enabled;
            
            if (previousMode != enabled)
            {
                Log($"PHYSICS MODE [{vehicleKey}]: {(enabled ? "ENABLED (atmosphere)" : "DISABLED (vacuum)")}");
            }
        }
        
        /// <summary>Returns whether a remote vehicle is in physics mode.</summary>
        public static bool IsInPhysicsMode(string vehicleKey)
        {
            return _physicsMode.GetValueOrDefault(vehicleKey, false);
        }
        
        /// <summary>Returns the vehicle key for a remote vehicle.</summary>
        public static string? GetVehicleKey(Vehicle vehicle)
        {
            if (vehicle == null) return null;
            return _vehicleKeys.TryGetValue(vehicle.Id, out string? key) ? key : null;
        }
        
        /// <summary>Re-applies the last received nozzle thrust for a remote vessel before its plume is built.</summary>
        public static void AddVolumetricExhaustInstancesPrefix(Vehicle __instance)
        {
            if (__instance == null || !IsRemoteVehicle(__instance))
                return;

            if (!VesselPositionUpdate.LastAppliedThrusts.TryGetValue(__instance, out float[]? thrusts)
                || thrusts == null || thrusts.Length == 0)
                return;

            VesselPositionUpdate.ApplyThrustsTo(__instance, thrusts);
            DriveRemotePlumeFx(__instance, thrusts);

            // Log the nozzle values present in the render buffer.
            var nozzles = __instance.Parts?.RocketNozzles;
            if (nozzles != null && nozzles.States.Length > 0)
            {
                float maxSent = 0f;
                for (int i = 0; i < thrusts.Length; i++)
                    if (thrusts[i] > maxSent) maxSent = thrusts[i];

                float maxDuty = 0f, maxThrust = 0f;
                var states = nozzles.States;
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].DutyCycle > maxDuty) maxDuty = states[i].DutyCycle;
                    if (states[i].AverageThrustFraction > maxThrust) maxThrust = states[i].AverageThrustFraction;
                }

                if (maxSent > 0f)
                {
                    ModLogger.LogThrottled(LogName, "PLUME_READBACK",
                        $"PLUME {__instance.Id}: sent max={maxSent:F2} -> readback DutyCycle={maxDuty:F2}, AvgThrust={maxThrust:F2}, nozzles={states.Length}");
                }
            }
        }

        /// <summary>Runs the engine FX chain locally for a remote vessel from its received thrusts.</summary>
        private static void DriveRemotePlumeFx(Vehicle vehicle, float[] thrusts)
        {
            try
            {
                var nozzles = vehicle.Parts?.RocketNozzles;
                var cores = vehicle.Parts?.RocketCores;
                if (nozzles == null || cores == null || nozzles.States.Length == 0)
                    return;

                float ambientPressure = vehicle.GetPhysicsStates().Environment.AtmosphericPressure;

                int count = Math.Min(thrusts.Length, nozzles.States.Length);

                // Resolve each core's throttle from the strongest of its nozzles.
                var coreThrottle = new Dictionary<int, float>();
                for (int i = 0; i < count; i++)
                {
                    RocketNozzle? m = nozzles.GetModuleByIdx(i);
                    if (m?.Rocket?.Core == null) continue;
                    int idx = m.Rocket.Core.StatesIdx;
                    if (!coreThrottle.TryGetValue(idx, out float existing) || thrusts[i] > existing)
                        coreThrottle[idx] = thrusts[i];
                }

                foreach (var kvp in coreThrottle)
                {
                    if (kvp.Key < 0 || kvp.Key >= cores.States.Length) continue;
                    var coreRef = cores.GetModuleAndAllMutableStatesForInitializationByIdx(kvp.Key);
                    coreRef.State.Throttle = kvp.Value;
                    coreRef.State.Conditions = coreRef.Module.ComputeConditions(kvp.Value);
                }

                for (int i = 0; i < count; i++)
                {
                    var nozzleRef = nozzles.GetModuleAndAllMutableStatesForInitializationByIdx(i);
                    RocketNozzle module = nozzleRef.Module;
                    if (module?.Rocket?.Core == null)
                        continue;

                    int coreIdx = module.Rocket.Core.StatesIdx;
                    if (coreIdx < 0 || coreIdx >= cores.States.Length)
                        continue;

                    RocketCoreState coreState = cores.States[coreIdx];

                    // Update nozzle state and plume data.
                    module.UpdateState(in coreState, null, ambientPressure, ref nozzleRef.State);
                    module.UpdatePlumeData(in coreState, ref nozzleRef.State, ambientPressure);

                    // Re-assert the transmitted per-nozzle thrust values.
                    nozzleRef.State.ThrustFraction = thrusts[i];
                    nozzleRef.State.AverageThrustFraction = thrusts[i];
                    nozzleRef.State.DutyCycle = thrusts[i] > 0f ? 1f : 0f;
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottled(LogName, "PLUME_FX_ERR",
                    $"DriveRemotePlumeFx failed for {vehicle.Id}: {ex.Message}");
            }
        }

        /// <summary>Skips analytic state population for remote vehicles not in physics mode.</summary>
        public static bool PopulateAnalyticStatesPrefix(object vehicleState)
        {
            try
            {
                // Read the ReadOnlyVehicle field by reflection.
                var vehicleField = vehicleState?.GetType().GetField("ReadOnlyVehicle");
                if (vehicleField != null)
                {
                    Vehicle? vehicle = vehicleField.GetValue(vehicleState) as Vehicle;
                    if (vehicle != null && IsRemoteVehicle(vehicle))
                    {
                        // Look up this vehicle's physics mode.
                        string? vehicleKey = GetVehicleKey(vehicle);
                        bool inPhysicsMode = vehicleKey != null && IsInPhysicsMode(vehicleKey);
                        
                        if (inPhysicsMode)
                        {
                            // Run the original method.
                            return true;
                        }
                        else
                        {
                            // Skip the original method.
                            return false;
                        }
                    }
                }
            }
            catch { }
            return true;
        }
        
        /// <summary>The one remote vessel currently permitted to join a physics bubble.</summary>
        public static Vehicle? BubbleGrantFor { get; set; }

        /// <summary>Keeps remote vessels out of physics bubbles.</summary>
        public static bool AddToBubblePrefix(Vehicle __instance, PhysicsBubble bubble)
        {
            if (__instance == null) return true;

            // Skip the original method when the bubble is null.
            if (bubble == null)
            {
                ModLogger.LogThrottled(LogName, $"NULLBUBBLE_{__instance.Id}",
                    $"{__instance.Id} was offered a null bubble - skipping rather than faulting");
                return false;
            }

            if (BubbleGrantFor != null && ReferenceEquals(__instance, BubbleGrantFor)) return true;
            if (!IsRemoteVehicle(__instance)) return true;

            ModLogger.LogThrottled(LogName, $"NOBUBBLE_{__instance.Id}",
                $"Keeping {__instance.Id} out of the physics loop - its owner simulates it");
            return false;
        }

        /// <summary>Counters for the pose resync.</summary>
        private static long _resyncCount;
        private static long _resyncSkipped;

        /// <summary>Advances every remote craft to the current frame's simulation instant.</summary>
        public static void ResyncRemotePosesToNow()
        {
            CelestialSystem? system = Universe.CurrentSystem;
            if (system == null) return;

            try
            {
                UniverseTime now = Universe.GetElapsedTime();
                ReadOnlySpan<Astronomical> all = system.All.AsSpan();

                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is not Vehicle v || v.IsDisposed) continue;
                    if (!IsRemoteVehicle(v)) continue;

                    Orbit? orbit = v.Orbit;
                    if (orbit == null) { _resyncSkipped++; continue; }

                    // Skip craft in contact with terrain.
                    if (v.Situation.HasTerrainContact()) { _resyncSkipped++; continue; }

                    StateVectors at = orbit.GetStateVectorsAt(now);

                    // Skip non-finite propagated states.
                    if (!IsFinite(at.PositionCci) || !IsFinite(at.VelocityCci) || at.TrueAnomaly.IsNaN())
                    {
                        _resyncSkipped++;
                        LogThrottled($"Cannot resync {v.Id} to the frame instant - " +
                                     "its orbit yields a non-finite state");
                        continue;
                    }

                    orbit.UpdatePosition(at);
                    v.UpdatePerFrameData();
                    _resyncCount++;
                }

                if (_resyncCount > 0 && (_resyncCount % 2000) < 2)
                    ModLogger.LogThrottled("Patches", "RESYNC_COUNT",
                        $"Frame-instant resync: {_resyncCount} remote poses advanced, " +
                        $"{_resyncSkipped} skipped");
            }
            catch (Exception ex)
            {
                _resyncSkipped++;
                LogThrottled($"Frame-instant resync failed: {ex.Message}");
            }
        }

        private static bool IsFinite(double3 v) =>
            !double.IsNaN(v.X) && !double.IsNaN(v.Y) && !double.IsNaN(v.Z) &&
            !double.IsInfinity(v.X) && !double.IsInfinity(v.Y) && !double.IsInfinity(v.Z);

        private static void LogThrottled(string msg) =>
            ModLogger.LogThrottled("Patches", "RESYNC_ERR", msg);
        
        /// <summary>Builds the world matrix for remote vessels, bypassing distance culling.</summary>
        public static bool GetWorldMatrixPrefix(Vehicle __instance, ref float4x4? __result, Camera camera)
        {
            if (!IsRemoteVehicle(__instance))
                return true;
            
            double3 vector = camera.GetPositionEgo(__instance);
            float4x4 translation = float4x4.CreateTranslation(new float3((float)vector.X, (float)vector.Y, (float)vector.Z));
            float4x4 rotation = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce));
            __result = rotation * translation;

            // Log per-frame render values while this vessel is watched.
            if (VesselStructure.ShouldWatchRender(__instance.Id))
            {
                double dist = vector.Length();
                bool bad = double.IsNaN(dist) || double.IsInfinity(dist);
                ModLogger.Log("Render",
                    $"FRAME {__instance.Id}: egoDist={(bad ? "INVALID" : dist.ToString("F1"))}m " +
                    $"parts={__instance.Parts?.Count ?? -1} " +
                    $"body2Cce=({__instance.Body2Cce.X:F3},{__instance.Body2Cce.Y:F3}," +
                    $"{__instance.Body2Cce.Z:F3},{__instance.Body2Cce.W:F3}) " +
                    $"sit={__instance.Situation} bubble={(__instance.PhysicsBubble != null)}");
            }

            return false;
        }
        
        /// <summary>Writes KSA console errors to the mod log.</summary>
        public static void LogCategoryErrorPrefix(string message, string sourceMemberName, string sourceFilePath, int sourceLineNumber)
        {
            try
            {
                ModLogger.LogAlways("Console", $"[ERROR] {message} ({sourceMemberName} in {sourceFilePath}:{sourceLineNumber})");
            }
            catch { }
        }
        
        /// <summary>Copies an existing CharacterAvatar into a KittenRenderable being built for a remote vehicle.</summary>
        public static bool KittenRenderableCtorPrefix(object __instance, string characterId)
        {
            if (!RemoteVehicleRenderer._creatingRemoteKittenEva)
                return true; // Run the original constructor.
            
            // Inject the existing renderable's CharacterAvatar.
            if (RemoteVehicleRenderer._existingRenderableForRemote != null)
            {
                try
                {
                    Log($"KittenRenderable patch: Copying CharacterAvatar from existing renderable for '{characterId}'");
                    
                    // Get the characterAvatar field on both objects.
                    var avatarField = __instance.GetType().GetField("characterAvatar", BindingFlags.NonPublic | BindingFlags.Instance);
                    var existingAvatarField = RemoteVehicleRenderer._existingRenderableForRemote.GetType().GetField("characterAvatar", BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    if (avatarField != null && existingAvatarField != null)
                    {
                        var existingAvatar = existingAvatarField.GetValue(RemoteVehicleRenderer._existingRenderableForRemote);
                        avatarField.SetValue(__instance, existingAvatar);
                        Log($"Successfully injected existing CharacterAvatar");
                        
                        // Copy the animation fields.
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
                        
                        return false; // Skip the original constructor.
                    }
                }
                catch (Exception ex)
                {
                    Log($"KittenRenderable patch failed: {ex.Message}");
                }
            }
            
            Log($"KittenRenderable patch: No existing renderable available, will run original constructor");
            return true; // Run the original constructor.
        }
    }
}
