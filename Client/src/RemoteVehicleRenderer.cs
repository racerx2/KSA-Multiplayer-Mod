using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using KSA;
using Brutal.Numerics;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Renders remote player vehicles by interpolating queued position updates.</summary>
    public class RemoteVehicleRenderer
    {
        private readonly EventSyncManager _syncManager;
        
        /// <summary>Remote vehicle objects by key (PlayerName_VehicleId)</summary>
        private readonly Dictionary<string, Vehicle> _remoteVehicles;
        
        /// <summary>Current position updates being interpolated</summary>
        private readonly ConcurrentDictionary<string, VesselPositionUpdate> _currentUpdates;
        
        /// <summary>Template ids already warned about.</summary>
        private readonly HashSet<string> _warnedMissingTemplates;
        private readonly Dictionary<string, DateTime> _nextCreationAttempts;
        
        /// <summary>True while a remote KittenEva is being created.</summary>
        public static bool _creatingRemoteKittenEva = false;
        
        /// <summary>Existing renderable to inject into remote KittenEva</summary>
        public static object? _existingRenderableForRemote = null;
        
        private const string LogName = "Renderer";
        private int _updateCounter = 0;

        /// <summary>Last logged position for each remote vessel.</summary>
        private readonly Dictionary<string, double3> _lastLoggedPose = new();
        
        private static readonly PropertyInfo? Body2CceProperty = typeof(Vehicle).GetProperty("Body2Cce");
        
        private SubspaceManager? _subspaceManager;
        
        public int RemoteVehicleCount => _remoteVehicles.Count;

        /// <summary>The remote vessels currently held, keyed by uid.</summary>
        public IReadOnlyDictionary<string, Vehicle> RemoteVehicles => _remoteVehicles;

        /// <summary>Vehicle creations staged until the frame sync point.</summary>
        private readonly Dictionary<string, EventSyncManager.RemoteVehicleData> _pendingCreations = new();

        /// <summary>Vessels consumed by a dock, and when they may be created again.</summary>
        private readonly Dictionary<string, DateTime> _consumedByDock = new();

        private static readonly TimeSpan ConsumedSuppression = TimeSpan.FromSeconds(15);
        
        public void SetSubspaceManager(SubspaceManager? manager)
        {
            _subspaceManager = manager;
        }
        
        public RemoteVehicleRenderer(EventSyncManager syncManager)
        {
            _syncManager = syncManager;
            _remoteVehicles = new Dictionary<string, Vehicle>();
            _currentUpdates = new ConcurrentDictionary<string, VesselPositionUpdate>();
            _warnedMissingTemplates = new HashSet<string>();
            _nextCreationAttempts = new Dictionary<string, DateTime>();
            Log("RemoteVehicleRenderer initialized (LMP-style interpolation)");
        }
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public Vehicle? GetRemoteVehicle(string key)
        {
            return _remoteVehicles.TryGetValue(key, out var vehicle) ? vehicle : null;
        }

        /// <summary>Updates remote vehicles once per frame.</summary>
        public void Update(double deltaTime)
        {
            if (!MultiplayerSettings.Current.EnableVesselSync || Universe.CurrentSystem == null)
                return;
            
            var remoteData = _syncManager.GetRemoteVehicles();

            // Keys to purge from the sync data after the loop.
            List<string>? purgeAfterDock = null;

            // Create new vehicles and ensure queues exist
            foreach (var kvp in remoteData)
            {
                string key = kvp.Key;
                var data = kvp.Value;
                
                if (!_remoteVehicles.ContainsKey(key))
                {
                    bool canRetry = !_nextCreationAttempts.TryGetValue(
                        key, out DateTime nextAttempt) ||
                        DateTime.UtcNow >= nextAttempt;
                    if (data.HasCurrentState &&
                        !string.IsNullOrEmpty(data.TemplateId) &&
                        canRetry)
                    {
                        if (SuppressedAfterDock(key, data))
                        {
                            (purgeAfterDock ??= new List<string>()).Add(key);
                            continue;
                        }
                        _nextCreationAttempts[key] =
                            DateTime.UtcNow.AddSeconds(2);

                        // Stage the creation for the frame sync point.
                        _pendingCreations[key] = data;
                    }
                }
            }
            
            if (purgeAfterDock != null)
            {
                foreach (string key in purgeAfterDock)
                    _syncManager.RemoveRemoteVehicle(key);
            }

            // Apply interpolated updates to all remote vehicles
            ApplyInterpolatedUpdates();
            
            // Remove vehicles that are no longer in remote data
            var keysToRemove = new List<string>();
            foreach (var key in _remoteVehicles.Keys)
            {
                if (!remoteData.ContainsKey(key))
                    keysToRemove.Add(key);
            }
            
            foreach (var key in keysToRemove)
                DestroyRemoteVehicle(key);
        }

        /// <summary>Applies interpolated updates to all remote vehicles.</summary>
        private void ApplyInterpolatedUpdates()
        {
            RebuildVesselsWhoseDesignChanged();
            EvictRemoteVehiclesFromBubbles();
            LogRailsState();

            foreach (var kvp in _currentUpdates)
            {
                var update = kvp.Value;
                
                if (update.Vessel == null)
                {
                    // Try to get vehicle reference
                    if (_remoteVehicles.TryGetValue(kvp.Key, out var vehicle))
                    {
                        update.Vessel = vehicle;
                    }
                    else
                    {
                        continue;
                    }
                }
                
                // Apply interpolated position
                update.ApplyInterpolatedUpdate(_subspaceManager!);
            }
            
            // Periodic logging
            _updateCounter++;
            if (_updateCounter % 300 == 0)
            {
                foreach (var kvp in _currentUpdates)
                {
                    var update = kvp.Value;
                    var queue = PositionUpdateQueue.GetQueue(kvp.Key);
                    int queueSize = queue?.Count ?? 0;
                    
                    Log($"INTERPOLATION [{kvp.Key}]: Frame={update.CurrentFrame:F0}/{update.NumFrames}, " +
                        $"Lerp={update.LerpPercentage:P0}, Queue={queueSize}, Sit={update.Situation}");

                    // Log the vessel's situation, rails flag, bubble population and pose delta.
                    Vehicle? v = update.Vessel;
                    if (v != null && !v.IsDisposed)
                    {
                        double3 now = v.GetPositionCce();
                        string delta = _lastLoggedPose.TryGetValue(kvp.Key, out double3 prev)
                            ? $"{(now - prev).Length():F2}m since last sample"
                            : "first sample";
                        _lastLoggedPose[kvp.Key] = now;

                        Log($"  POSE [{kvp.Key}]: sit={v.Situation} onRails={v.Situation.IsOnRails()} " +
                            $"bubble={(v.PhysicsBubble != null ? v.PhysicsBubble.NumVehicles.ToString() : "none")} " +
                            $"vehicles, {delta}");
                    }
                }
            }
        }

        // A second OnVehicleStateReceived stood here, queueing incoming state
        // the same way EventSyncManager's does. Only the sync manager's is
        // subscribed to NetworkPatches.OnVehicleStateReceived; this one was
        // never called, and wiring it up as well would have enqueued every
        // sample twice.

        private void CreateRemoteVehicle(string key, EventSyncManager.RemoteVehicleData data)
        {
            Log($"CreateRemoteVehicle CALLED for key={key}, vehicleId={data.VehicleId}, template={data.TemplateId}");
            
            if (Universe.CurrentSystem == null || string.IsNullOrEmpty(data.TemplateId))
            {
                Log($"Skip {key}: no system or template");
                return;
            }
            
            string playerName = data.OwnerName;

            // Derive the local vessel name from the uid.
            string uid = data.VesselUid;
            string vehicleId = VesselIdentity.LocalNameFor(uid, _syncManager.LocalPlayerName);

            // Skip creation while a queued split will produce this vessel.
            if (VesselStructure.IsAwaitingSplit(uid))
            {
                ModLogger.Log(LogName,
                    $"  HELD         : creation of {uid} deferred - a queued split will produce it");
                ModLogger.LogThrottled(LogName, $"AWAIT_{key}",
                    $"Holding creation of {uid}: a split is queued that will produce it");
                return;
            }

            // Ignore updates for a vessel that is ours, not a remote one.
            if (!VesselIdentity.IsRemoteName(vehicleId))
            {
                ModLogger.LogThrottled(LogName, $"ECHO_{key}",
                    $"Ignoring {playerName}'s update for {uid}: that vessel is ours, not theirs");
                return;
            }
            
            // Check whether the vehicle already exists in the Universe.
            var existingAstro = Universe.CurrentSystem.Get(vehicleId);
            if (existingAstro != null)
            {
                if (existingAstro is Vehicle existingVehicle)
                {
                    Log($"Recovering existing Universe vehicle {vehicleId}");
                    // Ensure the recovered vessel is in its parent's child list.
                    if (existingVehicle.Parent is IParentBody recoveredParent)
                        AttachToParent(existingVehicle, recoveredParent);
                    VehiclePatches.RegisterRemoteVehicle(existingVehicle, data.OwnerName, key);
                    _remoteVehicles[key] = existingVehicle;
                    AttachInterpolationState(key, data, existingVehicle);
                }
                else
                {
                    Log($"Universe id collision for non-vehicle {vehicleId}");
                }
                return;
            }
            
            string parentId = string.IsNullOrEmpty(data.ParentBodyId) ? "Earth" : data.ParentBodyId;
            Astronomical? parent = Universe.CurrentSystem.Get(parentId);
            if (parent == null)
                parent = Universe.CurrentSystem.Get("Earth");
            if (parent == null || parent is not Celestial parentCelestial)
            {
                Log($"Skip {key}: parent '{parentId}' not found");
                return;
            }
            
            Vehicle? remoteVehicle = TryCreateFromSharedDesign(
                vehicleId, parentCelestial, data);
            if (remoteVehicle == null)
            {
                VehicleTemplate? template = null;
                try { template = ModLibrary.Get<VehicleTemplate>(data.TemplateId); }
                catch (Exception ex)
                {
                    Log($"Template lookup failed for '{data.TemplateId}': {ex.Message}");
                }

                if (template == null)
                {
                    template = Program.ControlledVehicle?.BodyTemplate as VehicleTemplate ??
                        SessionUniverseManager.ProxyTemplate;
                    if (!_warnedMissingTemplates.Contains(data.TemplateId))
                    {
                        _warnedMissingTemplates.Add(data.TemplateId);
                        string alertMsg = template == null
                            ? $"{playerName} has vessel '{data.TemplateId}' with no usable design"
                            : $"Using a local proxy model for {playerName}'s ship";
                        TimedAlert.Create(
                            alertMsg, new byte4(255, 165, 0, 255), 5.0);
                        Log($"MISSING TEMPLATE: {alertMsg}");
                    }
                }
                if (template == null)
                    return;

                try
                {
                    remoteVehicle = template.CreateInto(
                        Universe.CurrentSystem, parentCelestial, vehicleId);
                    AttachToParent(remoteVehicle, parentCelestial);
                    Log($"Template CreateInto succeeded for {vehicleId}");
                }
                catch (Exception ex)
                {
                    ModLogger.LogAlways(LogName,
                        $"TEMPLATE CREATION FAILED for {vehicleId}: {ex}");
                    return;
                }
            }

            try
            {
                UniverseTime localTime = Universe.GetElapsedTime();
                double3 positionCci = data.TargetPosition;
                double3 velocityCci = data.TargetVelocity;
                if (data.LastSituation >= 2 && data.TargetPositionCcf.Length() > 1)
                {
                    doubleQuat ccf2Cci = parentCelestial.GetCcf2Cci(localTime);
                    positionCci = data.TargetPositionCcf.Transform(ccf2Cci);
                    double3 omega = new double3(0, 0, parentCelestial.GetAngularVelocity());
                    velocityCci = data.TargetVelocityCcf.Transform(ccf2Cci) +
                        double3.Cross(omega, positionCci);
                }

                Orbit orbit = Orbit.CreateFromStateCci(parentCelestial, localTime,
                    positionCci, velocityCci, remoteVehicle.OrbitColor);
                remoteVehicle.Teleport(orbit, null, null);
                remoteVehicle.UpdatePerFrameData();
                
                // Register the vehicle as remote.
                VehiclePatches.RegisterRemoteVehicle(remoteVehicle, data.OwnerName, key);
                
                _remoteVehicles[key] = remoteVehicle;
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName,
                    $"CREATION FAILED for {vehicleId}: {ex}");
                if (ex.InnerException != null)
                {
                    Log($"Inner Exception: {ex.InnerException.Message}");
                    if (ex.InnerException.InnerException != null)
                    {
                        Log($"Inner Inner: {ex.InnerException.InnerException.Message}");
                    }
                }
                Log($"Stack: {ex.StackTrace}");
                
                // Clean up partial creation
                if (remoteVehicle != null)
                {
                    try
                    {
                        Universe.CurrentSystem?.Deregister(remoteVehicle);
                        // Dispose is not called here.
                    }
                    catch { }
                }
                return;
            }
            
            AttachInterpolationState(key, data, remoteVehicle);
            _nextCreationAttempts.Remove(key);

            ModLogger.LogAlways(LogName,
                $"CREATED {vehicleId} for {data.OwnerName} with shared-world interpolation");
        }

        private static Vehicle? TryCreateFromSharedDesign(
            string vehicleId,
            Celestial parentCelestial,
            EventSyncManager.RemoteVehicleData data)
        {
            if (Universe.CurrentSystem == null ||
                data.CompressedDesignXml == null ||
                data.CompressedDesignXml.Length == 0)
            {
                return null;
            }

            try
            {
                using var compressed = new MemoryStream(data.CompressedDesignXml);
                using var brotli = new BrotliStream(
                    compressed, CompressionMode.Decompress);
                if (VehicleSaves.VehicleSerializer.Deserialize(brotli)
                    is not VehicleSaveData design ||
                    design.RootPartInstance == null)
                {
                    Log($"Shared design for {vehicleId} contained no part tree");
                    return null;
                }

                design.OnDataLoad(Mod.Empty);
                PartTree parts = PartTree.Deserialize(design.RootPartInstance);
                UniverseTime localTime = Universe.GetElapsedTime();
                double3 positionCci = data.TargetPosition;
                double3 velocityCci = data.TargetVelocity;
                if (data.LastSituation >= 2 &&
                    data.TargetPositionCcf.Length() > 1)
                {
                    doubleQuat ccf2Cci =
                        parentCelestial.GetCcf2Cci(localTime);
                    positionCci = data.TargetPositionCcf.Transform(ccf2Cci);
                    double3 omega = new double3(
                        0, 0, parentCelestial.GetAngularVelocity());
                    velocityCci =
                        data.TargetVelocityCcf.Transform(ccf2Cci) +
                        double3.Cross(omega, positionCci);
                }

                Orbit orbit = Orbit.CreateFromStateCci(
                    parentCelestial, localTime, positionCci, velocityCci,
                    new byte4(91, 192, 255, 255));
                Vehicle vehicle = Vehicle.CreateVehicle(
                    Universe.CurrentSystem,
                    data.TargetOrientation,
                    double3.Zero,
                    parentCelestial,
                    vehicleId,
                    parts.Root,
                    orbit);
                AttachToParent(vehicle, parentCelestial);
                vehicle.Parts.SequenceList.SetActiveSequence(
                    design.ActiveSequence);
                vehicle.Parts.SequenceList.ApplyEnvironments(
                    design.SequenceEnvironments);
                vehicle.Parts.FuelLinks.ApplySaveData(
                    design.FuelLinks, design.RootPartInstance);

                // Recompute derived part data so nozzle plume effects resolve.
                vehicle.Parts.RecomputeAllDerivedData();

                int withExhaust = 0;
                var nozzleStates = vehicle.Parts.RocketNozzles;
                if (nozzleStates != null)
                {
                    var fx = nozzleStates.FxStates;
                    for (int i = 0; i < fx.Length; i++)
                        if (fx[i].VolumetricExhaust != null) withExhaust++;
                }

                Log($"SHARED DESIGN CREATED {vehicleId}: " +
                    $"{data.CompressedDesignXml.Length} compressed bytes, " +
                    $"{vehicle.Parts.Count} parts, " +
                    $"{withExhaust}/{nozzleStates?.States.Length ?? 0} nozzles with exhaust FX");
                return vehicle;
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName,
                    $"SHARED DESIGN CREATION FAILED for {vehicleId}: {ex}");
                return null;
            }
        }

        /// <summary>Adds a remote vessel to its parent body's child list.</summary>
        private static void AttachToParent(Vehicle vehicle, IParentBody parent)
        {
            try
            {
                foreach (IOrbiter existing in parent.Children)
                {
                    if (ReferenceEquals(existing, vehicle))
                        return;
                }
                parent.Children.Add(vehicle);
                Log($"Attached {vehicle.Id} to {(parent as Astronomical)?.Id ?? "parent"} - eligible for a physics bubble");
            }
            catch (Exception ex)
            {
                Log($"ERROR attaching {vehicle.Id} to parent: {ex.Message}");
            }
        }

        private void AttachInterpolationState(
            string key, EventSyncManager.RemoteVehicleData data, Vehicle remoteVehicle)
        {
            if (!_currentUpdates.ContainsKey(key))
            {
                var update = new VesselPositionUpdate
                {
                    VehicleKey = key,
                    ParentBodyId = data.ParentBodyId ?? "Earth",
                    PositionCci = data.TargetPosition,
                    VelocityCci = data.TargetVelocity,
                    PositionCcf = data.TargetPositionCcf,
                    VelocityCcf = data.TargetVelocityCcf,
                    Orientation = data.TargetOrientation,
                    GameTimeStamp = data.SenderStateTimeSeconds,
                    ArrivalTimeSeconds = VesselPositionUpdate.GetMonotonicSeconds(),
                    Situation = data.LastSituation,
                    Vessel = remoteVehicle
                };
                _currentUpdates[key] = update;
            }
            else
            {
                _currentUpdates[key].Vessel = remoteVehicle;
            }
        }
        
        /// <summary>
        /// Keeps remote vessels out of every local physics bubble.
        /// </summary>
        /// <remarks>
        /// This is load-bearing, not diagnostic. A remote vessel is a real
        /// Vehicle the owner simulates and this client only replays; letting it
        /// join a bubble would hand the local physics step authority over a
        /// vessel it has no inputs for, and the replayed pose would then fight
        /// the solver every frame. It runs unconditionally, separate from the
        /// logging below, so silencing the logs can never switch it off.
        /// The one exception is a vessel a structure replay is holding, named by
        /// <see cref="VehiclePatches.BubbleGrantFor"/>: docking and staging need
        /// it in the bubble for the frame the merge or split is applied.
        /// </remarks>
        private void EvictRemoteVehiclesFromBubbles()
        {
            foreach (var kvp in _remoteVehicles)
            {
                Vehicle v = kvp.Value;
                if (v == null || v.IsDisposed) continue;

                if (v.PhysicsBubble != null && !ReferenceEquals(v, VehiclePatches.BubbleGrantFor))
                {
                    try
                    {
                        v.RemoveFromBubble(v.PhysicsBubble);
                    }
                    catch (Exception ex)
                    {
                        // One vessel refusing eviction must not strand the rest.
                        ModLogger.LogThrottledAlways("Rails", $"EVICT_ERR_{kvp.Key}",
                            $"{v.Id}: could not be removed from its physics bubble: {ex.Message}");
                        continue;
                    }

                    ModLogger.LogThrottledEvery("Rails", $"EVICT_{kvp.Key}",
                        $"{v.Id}: removed from its physics bubble - owner simulates it");
                }
            }
        }

        /// <summary>Logs the rails state of the controlled and remote vessels.</summary>
        private void LogRailsState()
        {
            // Routine telemetry: the player's debug-logging setting silences it.
            if (!MultiplayerSettings.Current.EnableDebugLogging)
                return;

            try
            {
                UniverseTime now = Universe.GetElapsedTime();

                // Log the controlled vessel's rails state, plan expiry and bubble population.
                Vehicle? mine = Program.ControlledVehicle;
                if (mine != null && !mine.IsDisposed)
                {
                    double myMargin = (mine.FlightPlan.ExpiryGameTime - now).Seconds();
                    int bubblePop = mine.PhysicsBubble?.NumVehicles ?? 0;
                    ModLogger.LogThrottledEvery("Rails", "RAILS_LOCAL",
                        $"LOCAL {mine.Id}: sit={mine.Situation} onRails={mine.Situation.IsOnRails()} " +
                        $"planExpiresIn={myMargin:F1}s bubbleVehicles={bubblePop} " +
                        $"simSpeed={Universe.GetSimulationSpeed():F2}");
                }

                foreach (var kvp in _remoteVehicles)
                {
                    Vehicle v = kvp.Value;
                    if (v == null || v.IsDisposed) continue;

                    Situation sit = v.Situation;
                    double expiryMargin = (v.FlightPlan.ExpiryGameTime - now).Seconds();

                    // Find the highest rocket core throttle on the vessel.
                    float maxCoreThrottle = 0f;
                    var cores = v.Parts?.RocketCores;
                    if (cores != null)
                    {
                        for (int i = 0; i < cores.NumModules; i++)
                        {
                            float t = cores.States[i].Throttle;
                            if (t > maxCoreThrottle) maxCoreThrottle = t;
                        }
                    }

                    float maxThrust = 0f;
                    if (VesselPositionUpdate.LastAppliedThrusts.TryGetValue(v, out float[]? lastThrusts)
                        && lastThrusts != null)
                    {
                        foreach (float t in lastThrusts)
                            if (t > maxThrust) maxThrust = t;
                    }

                    ModLogger.LogThrottledEvery("Rails", $"RAILS_{kvp.Key}",
                        $"{v.Id}: sit={sit} onRails={sit.IsOnRails()} " +
                        $"planExpiresIn={expiryMargin:F1}s complete={v.FlightPlan.IsComplete} " +
                        $"bubble={(v.PhysicsBubble != null)} " +
                        $"maxCoreThrottle={maxCoreThrottle:F3} lastThrustMax={maxThrust:F3}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottledAlways("Rails", "RAILS_ERR", $"rails probe failed: {ex.Message}");
            }
        }

        private void RebuildVesselsWhoseDesignChanged()
        {
            var remoteData = _syncManager.GetRemoteVehicles();
            List<string>? toRebuild = null;

            foreach (var kvp in remoteData)
            {
                if (!kvp.Value.DesignChanged) continue;

                // Only rebuild what we have actually built.
                if (!_remoteVehicles.TryGetValue(kvp.Key, out Vehicle? existing))
                {
                    kvp.Value.DesignChanged = false;
                    continue;
                }

                // Skip the rebuild while a structure event for it is queued.
                if (VesselStructure.HasPendingStructureFor(kvp.Key))
                {
                    ModLogger.LogThrottled(LogName, $"REBUILD_WAIT_{kvp.Key}",
                        $"Holding the rebuild of {kvp.Key} - a structure event for it is still queued");
                    continue;
                }

                // Skip the rebuild while the vessel is the one being flown.
                if (ReferenceEquals(Program.ControlledVehicle, existing))
                {
                    ModLogger.LogThrottled(LogName, $"REBUILD_CTRL_{kvp.Key}",
                        $"Holding the rebuild of {kvp.Key} - it is the vessel being flown");
                    continue;
                }

                kvp.Value.DesignChanged = false;
                (toRebuild ??= new List<string>()).Add(kvp.Key);
            }

            if (toRebuild == null) return;

            foreach (string key in toRebuild)
            {
                Log($"REBUILD {key}: design changed, recreating from the owner's new part tree");
                DestroyRemoteVehicle(key);
                // Creation happens on the next state update.
            }
        }

        /// <summary>Adopts an existing vessel as a remote vehicle.</summary>
        public void AdoptVessel(string key, Vehicle vehicle, string ownerName)
        {
            if (string.IsNullOrEmpty(key) || vehicle == null) return;

            VehiclePatches.RegisterRemoteVehicle(vehicle, ownerName, key);
            _remoteVehicles[key] = vehicle;
            _nextCreationAttempts.Remove(key);

            // Lift any dock suppression for this key.
            if (_consumedByDock.Remove(key))
                Log($"{key} is back from a dock - suppression lifted");

            var remoteData = _syncManager.GetRemoteVehicles();
            if (remoteData.TryGetValue(key, out var data))
                AttachInterpolationState(key, data, vehicle);

            Log($"  ADOPTED      : {vehicle.Id} for {key} - rendering immediately, no retry wait");
        }

        /// <summary>Returns true while a dock-consumed vessel should stay gone.</summary>
        private bool SuppressedAfterDock(string key, EventSyncManager.RemoteVehicleData data)
        {
            if (!_consumedByDock.TryGetValue(key, out DateTime until)) return false;

            if (data.DesignChanged)
            {
                _consumedByDock.Remove(key);
                Log($"{key} has a new design after being docked away - allowing it back");
                return false;
            }

            if (DateTime.UtcNow >= until)
            {
                // Suppression window has elapsed; allow the vessel back.
                _consumedByDock.Remove(key);
                Log($"{key} suppression after the dock has lapsed");
                return false;
            }

            ModLogger.LogThrottled(LogName, $"DOCKGONE_{key}",
                $"Ignoring state for {key}: a dock consumed it, and its owner has not caught up yet");
            return true;
        }

        /// <summary>Performs the staged vehicle creations at the frame sync point.</summary>
        public void DrainDeferredWork()
        {
            if (_pendingCreations.Count == 0) return;

            var staged = new List<KeyValuePair<string, EventSyncManager.RemoteVehicleData>>(_pendingCreations);
            _pendingCreations.Clear();

            foreach (var kvp in staged)
            {
                if (_remoteVehicles.ContainsKey(kvp.Key)) continue;
                try
                {
                    CreateRemoteVehicle(kvp.Key, kvp.Value);
                }
                catch (Exception ex)
                {
                    ModLogger.LogAlways(LogName,
                        $"Deferred creation of {kvp.Key} failed: {ex.Message}");
                }
            }
        }

        /// <summary>Drops our object for a vessel about to be rebuilt, keeping its sync data.</summary>
        public void ForgetRemoteVehicleForRebuild(string key)
        {
            if (!_remoteVehicles.Remove(key))
                return;

            _nextCreationAttempts.Remove(key);
            _currentUpdates.TryRemove(key, out _);
            _pendingCreations.Remove(key);
            PositionUpdateQueue.RemoveQueue(key);
            Log($"Released {key} so the replayed split can produce it - data kept");
        }

        public void ForgetRemoteVehicle(string key)
        {
            if (!_remoteVehicles.Remove(key))
                return;

            _nextCreationAttempts.Remove(key);
            _currentUpdates.TryRemove(key, out _);
            _pendingCreations.Remove(key);
            PositionUpdateQueue.RemoveQueue(key);
            _consumedByDock[key] = DateTime.UtcNow + ConsumedSuppression;
            Log($"Forgot {key} - consumed by a dock; ignoring its state for " +
                $"{ConsumedSuppression.TotalSeconds:F0}s unless a new design arrives");
        }

        /// <summary>Clears every other vessel's target that points at the given vessel.</summary>
        public static void ReleaseTargetsOn(Vehicle doomed)
        {
            CelestialSystem? system = Universe.CurrentSystem;
            if (system == null || doomed == null) return;

            try
            {
                ReadOnlySpan<Astronomical> all = system.All.AsSpan();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is Vehicle other && !ReferenceEquals(other, doomed) &&
                        !other.IsDisposed && ReferenceEquals(other.Target, doomed))
                    {
                        other.SetTarget(null);
                        Log($"Cleared {other.Id}'s target - it pointed at {doomed.Id}, which is being replaced");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Could not release targets on {doomed.Id}: {ex.Message}");
            }
        }

        private void DestroyRemoteVehicle(string key)
        {
            if (!_remoteVehicles.TryGetValue(key, out Vehicle? vehicle))
                return;
            
            VehiclePatches.UnregisterRemoteVehicle(vehicle);
            ReleaseTargetsOn(vehicle);
            Universe.CurrentSystem?.Deregister(vehicle);
            vehicle.Dispose();
            
            _remoteVehicles.Remove(key);
            _nextCreationAttempts.Remove(key);
            _currentUpdates.TryRemove(key, out _);
            PositionUpdateQueue.RemoveQueue(key);
            
            Log($"Destroyed {key}");
        }
        
        public void Dispose()
        {
            foreach (var key in new List<string>(_remoteVehicles.Keys))
                DestroyRemoteVehicle(key);
            
            _remoteVehicles.Clear();
            _currentUpdates.Clear();
            _warnedMissingTemplates.Clear();
            _nextCreationAttempts.Clear();
            PositionUpdateQueue.ClearAllQueues();
        }
        
        // A RemovePlayerVehicles that matched keys by "{playerName}_" prefix
        // stood here. Keys are uids from VesselIdentity, separated by '|'
        // because KSA ids contain underscores, so the prefix never matched.
        // A departing player's vessels are dropped by the sync manager, whose
        // records this renderer follows on the next pass.
    }
}
