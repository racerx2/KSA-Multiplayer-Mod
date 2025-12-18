using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using KSA;
using Brutal.Numerics;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Renders remote player vehicles using LMP-style interpolation.
    /// 
    /// Architecture (modeled after Luna Multiplayer):
    /// 1. Incoming position updates are queued in PositionUpdateQueue
    /// 2. VesselPositionUpdate holds current + target state for interpolation
    /// 3. Each frame, we interpolate between current and target positions
    /// 4. When interpolation finishes, we dequeue the next target
    /// 
    /// KSA-specific adaptations:
    /// - Uses Orbit.CreateFromStateCci() for orbit creation
    /// - Handles KSA situations (Freefall, Maneuvering, etc.)
    /// - Remote vehicles are "immortal" (excluded from physics simulation)
    /// </summary>
    public class RemoteVehicleRenderer
    {
        private readonly EventSyncManager _syncManager;
        
        /// <summary>Remote vehicle objects by key (PlayerName_VehicleId)</summary>
        private readonly Dictionary<string, Vehicle> _remoteVehicles;
        
        /// <summary>Current position updates being interpolated</summary>
        private readonly ConcurrentDictionary<string, VesselPositionUpdate> _currentUpdates;
        
        /// <summary>Track templates we've already warned about (to avoid spam)</summary>
        private readonly HashSet<string> _warnedMissingTemplates;
        
        /// <summary>Flag indicating we're creating a remote KittenEva (for Harmony patch)</summary>
        public static bool _creatingRemoteKittenEva = false;
        
        /// <summary>Existing renderable to inject into remote KittenEva</summary>
        public static object? _existingRenderableForRemote = null;
        
        private const string LogName = "Renderer";
        private int _updateCounter = 0;
        
        private static readonly PropertyInfo? Body2CceProperty = typeof(Vehicle).GetProperty("Body2Cce");
        
        private SubspaceManager? _subspaceManager;
        
        public int RemoteVehicleCount => _remoteVehicles.Count;
        
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
            Log("RemoteVehicleRenderer initialized (LMP-style interpolation)");
        }
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public Vehicle? GetRemoteVehicle(string key)
        {
            return _remoteVehicles.TryGetValue(key, out var vehicle) ? vehicle : null;
        }

        /// <summary>
        /// Called every frame to update remote vehicles
        /// </summary>
        public void Update(double deltaTime)
        {
            if (!MultiplayerSettings.Current.EnableVesselSync || Universe.CurrentSystem == null)
                return;
            
            var remoteData = _syncManager.GetRemoteVehicles();
            
            // Create new vehicles and ensure queues exist
            foreach (var kvp in remoteData)
            {
                string key = kvp.Key;
                var data = kvp.Value;
                
                if (!_remoteVehicles.ContainsKey(key))
                {
                    if (data.HasCurrentState && !string.IsNullOrEmpty(data.TemplateId))
                    {
                        CreateRemoteVehicle(key, data);
                    }
                }
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

        /// <summary>
        /// Apply interpolated updates to all remote vehicles.
        /// This is the core LMP-style update loop.
        /// </summary>
        private void ApplyInterpolatedUpdates()
        {
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
                }
            }
        }

        /// <summary>
        /// Handle incoming position update - queue it for interpolation
        /// </summary>
        public void OnVehicleStateReceived(VehicleStateMessage msg)
        {
            string key = $"{msg.OwnerPlayerName}_{msg.VehicleId}";
            
            // Ensure queue exists
            var queue = PositionUpdateQueue.GetOrCreateQueue(key);
            queue.Enqueue(msg);
            
            // Ensure current update exists
            if (!_currentUpdates.ContainsKey(key))
            {
                _currentUpdates[key] = new VesselPositionUpdate(msg);
            }
        }

        private void CreateRemoteVehicle(string key, EventSyncManager.RemoteVehicleData data)
        {
            Log($"CreateRemoteVehicle CALLED for key={key}, vehicleId={data.VehicleId}, template={data.TemplateId}");
            
            if (Universe.CurrentSystem == null || string.IsNullOrEmpty(data.TemplateId))
            {
                Log($"Skip {key}: no system or template");
                return;
            }
            
            string playerName = data.OwnerName;
            string vehicleId = $"MP_{data.OwnerName}_{data.VehicleId}";
            
            // Check if vehicle already exists in Universe (from a previous failed creation attempt)
            var existingAstro = Universe.CurrentSystem.Get(vehicleId);
            if (existingAstro != null)
            {
                // Vehicle already exists - just skip, don't try to delete (causes crashes)
                Log($"Vehicle {vehicleId} already exists in Universe - skipping this frame");
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
            
            VehicleTemplate? template = null;
            try { template = ModLibrary.Get<VehicleTemplate>(data.TemplateId); }
            catch (Exception ex) { Log($"Template error: {ex.Message}"); }
            
            if (template == null)
            {
                // Template doesn't exist - show alert (only once per template)
                if (!_warnedMissingTemplates.Contains(data.TemplateId))
                {
                    _warnedMissingTemplates.Add(data.TemplateId);
                    string alertMsg = $"{playerName} has vessel '{data.TemplateId}' you don't have";
                    Alert.Create(alertMsg, new byte4(255, 165, 0, 255), 5.0);  // Orange color
                    Log($"MISSING TEMPLATE: {alertMsg}");
                }
                return;
            }
            
            // Handle KittenTemplates (EVA astronauts) - use CreateKitten factory method
            bool isEvaKitten = template is KittenTemplate;
            KittenTemplate? kittenTemplate = isEvaKitten ? (KittenTemplate)template : null;
            string? characterId = null;
            if (isEvaKitten && kittenTemplate != null)
            {
                // Get the character ID from the template - this is what we need to pass to CreateKitten
                characterId = kittenTemplate.Character?.Id;
                Log($"EVA kitten detected for '{data.TemplateId}' - CharacterId: {characterId ?? "null"}");
            }
            
            Vehicle? remoteVehicle = null;
            try
            {
                // For EVA kittens, we need to handle asset loading issues
                // The problem is KittenEva constructor creates KittenRenderable which loads assets
                // We'll use Harmony to bypass the renderable creation for remote vehicles
                if (isEvaKitten && !string.IsNullOrEmpty(characterId))
                {
                    Log($"Creating remote KittenEva for character '{characterId}'");
                    
                    // Find ANY existing KittenEva in the universe (we need its working renderable)
                    KittenEva? existingKitten = null;
                    foreach (Vehicle v in Universe.CurrentSystem.Vehicles.GetList())
                    {
                        if (v is KittenEva eva && v.Id != vehicleId)
                        {
                            Log($"Found existing KittenEva: {v.Id}");
                            existingKitten = eva;
                            break;
                        }
                    }
                    
                    if (existingKitten == null)
                    {
                        Log($"ERROR: No existing KittenEva found in universe - cannot create remote EVA");
                    }
                    else
                    {
                        Log($"Using renderable from existing KittenEva '{existingKitten.Id}'");
                        
                        // Get the existing renderable using reflection
                        var renderableField = typeof(KittenEva).GetField("_renderable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var existingRenderable = renderableField?.GetValue(existingKitten);
                        
                        if (existingRenderable != null)
                        {
                            Log($"Got existing renderable, marking next KittenEva as remote...");
                            
                            // Mark that we're creating a remote vehicle so our Harmony patch can skip renderable creation
                            _creatingRemoteKittenEva = true;
                            _existingRenderableForRemote = existingRenderable;
                            
                            try
                            {
                                // Create orbit first
                                SimTime senderTime = new SimTime(data.SenderStateTimeSeconds);
                                Orbit orbit = Orbit.CreateFromStateCci(parentCelestial, senderTime, 
                                    data.TargetPosition, data.TargetVelocity, FlightPlan.FirstPatchColor);
                                
                                // Get template from existing kitten
                                var templateField = typeof(KittenEva).GetField("_template", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                var existingTemplate = templateField?.GetValue(existingKitten) as KittenTemplate;
                                
                                if (existingTemplate != null)
                                {
                                    // Create the KittenEva - our Harmony patch will inject the existing renderable
                                    remoteVehicle = new KittenEva(Universe.CurrentSystem, existingTemplate, parent, vehicleId);
                                    
                                    Log($"KittenEva created for {vehicleId}");
                                    
                                    // Add to parent and set flight plan
                                    parent.Children.Add(remoteVehicle);
                                    var flightPlan = new FlightPlan(orbit, new KeyHash((uint)vehicleId.GetHashCode()));
                                    remoteVehicle.SetFlightPlan(flightPlan);
                                    remoteVehicle.UpdatePerFrameData();
                                }
                                else
                                {
                                    Log($"ERROR: Could not get template from existing KittenEva");
                                }
                            }
                            finally
                            {
                                _creatingRemoteKittenEva = false;
                                _existingRenderableForRemote = null;
                            }
                        }
                        else
                        {
                            Log($"ERROR: Could not extract renderable from existing KittenEva");
                        }
                    }
                }
                else
                {
                    remoteVehicle = template.CreateInto(Universe.CurrentSystem, parent, vehicleId);
                    Log($"CreateInto succeeded for {vehicleId}");
                
                    // Create parts from template
                    if (template.RootPartInstance != null)
                    {
                        Part rootPart = new Part(template.RootPartInstance, remoteVehicle);
                        remoteVehicle.Parts = new PartTree(rootPart);
                        
                        foreach (Part part in remoteVehicle.Parts.Parts)
                        {
                            part.SetVehicle(remoteVehicle);
                            part.Tree = remoteVehicle.Parts;
                        }
                        
                        remoteVehicle.UpdateVehicleConfiguration(isEditorUpdate: false);
                        Log($"Created parts for {vehicleId}: {remoteVehicle.Parts.Parts.Count} parts");
                    }
                    
                    // Add to parent's Children list for rendering
                    parent.Children.Add(remoteVehicle);
                    
                    // Create initial orbit
                    SimTime senderTime = new SimTime(data.SenderStateTimeSeconds);
                    Orbit orbit = Orbit.CreateFromStateCci(parentCelestial, senderTime, 
                        data.TargetPosition, data.TargetVelocity, remoteVehicle.OrbitColor);
                    var flightPlan = new FlightPlan(orbit, new KeyHash((uint)vehicleId.GetHashCode()));
                    remoteVehicle.SetFlightPlan(flightPlan);
                    remoteVehicle.UpdatePerFrameData();
                }
                
                // Register as remote vehicle (for physics exclusion)
                VehiclePatches.RegisterRemoteVehicle(remoteVehicle, data.OwnerName);
                
                _remoteVehicles[key] = remoteVehicle;
            }
            catch (Exception ex)
            {
                Log($"CREATION FAILED for {vehicleId}: {ex.Message}");
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
                        parent.Children.Remove(remoteVehicle);
                        Universe.CurrentSystem?.Deregister(remoteVehicle);
                        // Don't call Dispose() - it might crash
                    }
                    catch { }
                }
                return;
            }
            
            // Create interpolation state
            if (!_currentUpdates.ContainsKey(key))
            {
                var update = new VesselPositionUpdate
                {
                    VehicleKey = key,
                    ParentBodyId = data.ParentBodyId ?? "Earth",
                    PositionCci = data.TargetPosition,
                    VelocityCci = data.TargetVelocity,
                    Orientation = data.TargetOrientation,
                    GameTimeStamp = data.SenderStateTimeSeconds,
                    Situation = data.LastSituation,
                    Vessel = remoteVehicle
                };
                _currentUpdates[key] = update;
            }
            else
            {
                _currentUpdates[key].Vessel = remoteVehicle;
            }
            
            Log($"CREATED {vehicleId} with LMP-style interpolation");
        }
        
        private void DestroyRemoteVehicle(string key)
        {
            if (!_remoteVehicles.TryGetValue(key, out Vehicle? vehicle))
                return;
            
            // Remove from parent's Children list
            if (Universe.CurrentSystem != null)
            {
                foreach (var astro in Universe.CurrentSystem.All.GetList())
                {
                    if (astro.Children.Contains(vehicle))
                    {
                        astro.Children.Remove(vehicle);
                        break;
                    }
                }
            }
            
            VehiclePatches.UnregisterRemoteVehicle(vehicle);
            Universe.CurrentSystem?.Deregister(vehicle);
            vehicle.Dispose();
            
            _remoteVehicles.Remove(key);
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
            PositionUpdateQueue.ClearAllQueues();
        }
        
        public void RemovePlayerVehicles(string playerName)
        {
            var keysToRemove = new List<string>();
            foreach (var key in _remoteVehicles.Keys)
            {
                if (key.StartsWith(playerName + "_"))
                    keysToRemove.Add(key);
            }
            foreach (var key in keysToRemove)
            {
                DestroyRemoteVehicle(key);
                Log($"Removed vehicle for disconnected player: {key}");
            }
        }
    }
}
