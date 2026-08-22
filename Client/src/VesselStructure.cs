using System;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Replicates part-tree mutations across the network: staging, decoupling, undocking and docking.</summary>
    public static class VesselStructure
    {
        private const string LogName = "Structure";

        private static Harmony? _harmony;
        private static uint _sequence;

        /// <summary>Set while replaying a peer's operation to suppress re-broadcast.</summary>
        private static bool _replaying;

        /// <summary>Set when this pass deferred a replay waiting for a physics bubble.</summary>
        private static bool _deferredThisPass;

        public static string LocalPlayerName { get; set; } = string.Empty;

        /// <summary>Arrival time of each queued event, keyed by sequence number.</summary>
        private static readonly System.Collections.Generic.Dictionary<uint, DateTime> _arrivedAt = new();

        /// <summary>Vessels to watch frame by frame after a structural change, and until when.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, DateTime> _renderWatch = new();

        /// <summary>
        /// Number of entries in <see cref="_renderWatch"/>, readable without
        /// taking the lock.
        /// </summary>
        /// <remarks>
        /// <see cref="ShouldWatchRender"/> is called from the world-matrix
        /// patch, so it runs for every remote vessel on every camera on every
        /// frame, and nothing is being watched almost all of that time. Reading
        /// this first keeps the render path off the lock in that case.
        /// </remarks>
        private static volatile int _renderWatchCount;

        public static bool ShouldWatchRender(string vehicleId)
        {
            if (_renderWatchCount == 0) return false;

            lock (_renderWatch)
            {
                if (!_renderWatch.TryGetValue(vehicleId, out DateTime until)) return false;
                if (DateTime.UtcNow > until)
                {
                    _renderWatch.Remove(vehicleId);
                    _renderWatchCount = _renderWatch.Count;
                    return false;
                }
                return true;
            }
        }

        private static void WatchRender(string vehicleId, double seconds = 3.0)
        {
            lock (_renderWatch)
            {
                _renderWatch[vehicleId] = DateTime.UtcNow.AddSeconds(seconds);
                _renderWatchCount = _renderWatch.Count;
            }
        }

        public static event Action<VesselStructureMessage>? OnProduced;

        /// <summary>Raised at the frame sync point, after queued structure changes have been applied.</summary>
        public static event Action? OnSafeWindow;

        /// <summary>Raised with a vessel's local name once a merge has disposed it.</summary>
        public static event Action<string>? OnVesselConsumed;

        /// <summary>Raised with the local name of an impostor vessel being removed so a replayed split can produce the real one.</summary>
        public static event Action<string>? OnVesselReplaced;

        /// <summary>Raised with the local name of a vessel a split produced.</summary>
        public static event Action<string>? OnVesselCreated;

        /// <summary>Raised when a replayed split produces a vessel: (uid, vessel, owner).</summary>
        public static event Action<string, Vehicle, string>? OnRemoteVesselProduced;

        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>Logs the separation vector between two vessels and the first one's orientation.</summary>
        private static void LogGeometry(string tag, Vehicle a, Vehicle b)
        {
            try
            {
                if (a == null || b == null || a.IsDisposed || b.IsDisposed) return;

                double3 sep = b.GetPositionCce() - a.GetPositionCce();
                doubleQuat q = a.Body2Cce;

                Log($"  GEOM {tag}: {a.Id} -> {b.Id} sep=({sep.X:F2},{sep.Y:F2},{sep.Z:F2}) " +
                    $"|sep|={sep.Length():F2}m  {a.Id}.Body2Cce=({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4})");
            }
            catch (Exception ex)
            {
                Log($"  GEOM {tag}: unavailable - {ex.Message}");
            }
        }

        public static void ApplyPatches()
        {
            _harmony = new Harmony("com.ksa.mods.multiplayer.structure");

            var split = AccessTools.Method(typeof(Vehicle), "Split");
            if (split != null)
            {
                _harmony.Patch(split,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VesselStructure), nameof(SplitPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VesselStructure), nameof(SplitPostfix))));
                Log("Patched Vehicle.Split");
            }
            else Log("WARNING: Vehicle.Split not found - staging will not replicate");

            var merge = AccessTools.Method(typeof(Vehicle), "MergeFrom");
            if (merge != null)
            {
                _harmony.Patch(merge,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VesselStructure), nameof(MergePrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VesselStructure), nameof(MergePostfix))));
                Log("Patched Vehicle.MergeFrom");
            }
            else Log("WARNING: Vehicle.MergeFrom not found - docking will not replicate");

            // Add a multiplayer Dock entry to the stock docking port context menu.
            var portMenu = AccessTools.Method(typeof(DockingPort), "ShowContextMenu");
            if (portMenu != null)
            {
                _harmony.Patch(portMenu,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DockingAssist), nameof(DockingAssist.ShowContextMenuPostfix))));
                Log("Patched DockingPort.ShowContextMenu - multiplayer dock offered in the part menu");
            }
            else Log("WARNING: DockingPort.ShowContextMenu not found - no in-world dock option");

            // Hook the input-apply step to drain queued replays and correct undock control.
            var applyInput = AccessTools.Method(typeof(InputEvents), "ApplyInputEvents");
            if (applyInput != null)
            {
                _harmony.Patch(applyInput,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VesselStructure), nameof(Drain))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VesselStructure), nameof(AfterApplyInputEvents))));
                Log("Patched InputEvents.ApplyInputEvents - replays drain in the safe window, " +
                    "and undock control is corrected on the way out");
            }
            else Log("WARNING: InputEvents.ApplyInputEvents not found - replays would race the solver");

            // Forward a passenger's undock to the owner of the stack.
            UndockRequests.ApplyPatches(_harmony);

            // Install the read-only docking panel readout patches.
            DockingReadout.ApplyPatches(_harmony);
        }

        public static void RemovePatches()
        {
            _harmony?.UnpatchAll("com.ksa.mods.multiplayer.structure");
            _harmony = null;
        }

        // ---------------------------------------------------------------- addressing

        private static string UidOf(Vehicle v) =>
            VesselIdentity.UidFromLocalName(v.Id, LocalPlayerName);

        private static Vehicle? Resolve(string uid) =>
            Universe.CurrentSystem?.Get(VesselIdentity.LocalNameFor(uid, LocalPlayerName)) as Vehicle;

        /// <summary>Finds the vessel a uid names on this machine, or null.</summary>
        public static Vehicle? ResolveVessel(string uid) => Resolve(uid);

        /// <summary>Returns a vessel's docking port by index, or null if out of range.</summary>
        public static DockingPort? DockingPortAt(Vehicle v, int index) => PortAt(v, index);

        /// <summary>Finds a connector's kind and index within its vessel's decoupler or docking port list.</summary>
        private static bool TryAddress(Vehicle v, Part.Connector c, out byte kind, out int index)
        {
            kind = VesselStructureMessage.CONNECTOR_DECOUPLER;
            index = -1;

            var decouplers = v.Parts?.Decouplers;
            if (decouplers != null)
            {
                for (int i = 0; i < decouplers.NumModules; i++)
                {
                    if (ReferenceEquals(decouplers[i]?.Connector, c))
                    {
                        index = i;
                        return true;
                    }
                }
            }

            if (TryAddressPort(v, c, out index))
            {
                kind = VesselStructureMessage.CONNECTOR_DOCKING_PORT;
                return true;
            }
            return false;
        }

        private static bool TryAddressPort(Vehicle v, Part.Connector c, out int index)
        {
            index = -1;
            var ports = v.Parts?.DockingPorts;
            if (ports == null) return false;

            for (int i = 0; i < ports.NumModules; i++)
            {
                if (ReferenceEquals(ports[i]?.Connector, c))
                {
                    index = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Finds a docking port's index within its vessel's port list.</summary>
        /// <remarks>
        /// The index is how a port is named on the wire, and it is only usable
        /// because both machines build the same port list for the same vessel —
        /// the same assumption the structure replay already rests on.
        /// </remarks>
        public static bool TryAddressPort(Vehicle v, DockingPort port, out int index)
        {
            index = -1;
            if (v == null || port == null) return false;
            return TryAddressPort(v, port.Connector, out index);
        }

        /// <summary>Logs a vessel's decoupler list with each entry's index and part name.</summary>
        private static void LogDecouplerRoster(Vehicle v, string context)
        {
            try
            {
                var list = v.Parts?.Decouplers;
                if (list == null) { Log($"ROSTER {context} {v.Id}: no decouplers"); return; }

                var sb = new System.Text.StringBuilder();
                sb.Append($"ROSTER {context} {v.Id}: {list.NumModules} decouplers, {v.Parts.Count} parts [");
                for (int i = 0; i < list.NumModules; i++)
                {
                    var d = list[i];
                    string partName = d?.Parent?.FullPart?.Template?.Id ?? "?";
                    sb.Append($"{i}={partName}");
                    if (i < list.NumModules - 1) sb.Append(", ");
                }
                sb.Append(']');
                Log(sb.ToString());
            }
            catch (Exception ex) { Log($"ROSTER {context} failed: {ex.Message}"); }
        }

        private static Part.Connector? ConnectorAt(Vehicle v, byte kind, int index)
        {
            if (kind == VesselStructureMessage.CONNECTOR_DECOUPLER)
            {
                var list = v.Parts?.Decouplers;
                if (list == null || index < 0 || index >= list.NumModules) return null;
                return list[index]?.Connector;
            }
            var ports = v.Parts?.DockingPorts;
            if (ports == null || index < 0 || index >= ports.NumModules) return null;
            return ports[index]?.Connector;
        }

        private static DockingPort? PortAt(Vehicle v, int index)
        {
            var ports = v.Parts?.DockingPorts;
            if (ports == null || index < 0 || index >= ports.NumModules) return null;
            return ports[index];
        }

        // --------------------------------------------------- undock control correction

        /// <summary>The craft to keep flying across an undock, and the one not to be given.</summary>
        private static Vehicle? _undockKeep;
        private static Vehicle? _undockReject;
        private static Camera? _undockCamera;
        private static bool _undockWasFollowingKeep;

        /// <summary>Puts the camera and controls back on the locally owned craft after an undock moved them.</summary>
        public static void AfterApplyInputEvents()
        {
            // Advance remote craft poses to the current frame's instant.
            VehiclePatches.ResyncRemotePosesToNow();

            Vehicle? keep = _undockKeep;
            Vehicle? reject = _undockReject;
            Camera? camera = _undockCamera;
            bool wasFollowingKeep = _undockWasFollowingKeep;

            // Clear the one-shot state before doing any work with it.
            _undockKeep = null;
            _undockReject = null;
            _undockCamera = null;
            _undockWasFollowingKeep = false;

            if (keep == null || reject == null) return;

            try
            {
                if (keep.IsDisposed)
                {
                    Log($"UNDOCK: {keep.Id} was destroyed during the split - leaving control alone");
                    return;
                }

                if (ReferenceEquals(Program.ControlledVehicle, reject))
                {
                    Program.ControlledVehicle = keep;
                    Log($"UNDOCK: control stays on {keep.Id} - {reject.Id} belongs to its owner");
                }

                // Restore the camera only if it was following our craft before the undock.
                if (wasFollowingKeep && camera != null && ReferenceEquals(camera.Following, reject))
                {
                    camera.SetFollow(keep, camera.TidalLocking, changeControl: false, alert: false);
                    Log($"UNDOCK: camera stays on {keep.Id}");
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR correcting control after undock: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------- outbound

        public sealed class SplitState
        {
            public bool Found;
            public byte Kind;
            public int Index;
            public string Uid = string.Empty;
        }

        public static void SplitPrefix(Vehicle __instance, Part.Connector splitConnector,
                                       out SplitState __state)
        {
            __state = new SplitState();
            if (_replaying || __instance == null || splitConnector == null) return;

            // Announce only splits of locally created vessels.
            if (VesselIdentity.IsRemoteName(__instance.Id)) return;

            Log($"=== STAGE BEGIN (sender) === vessel={__instance.Id} " +
                $"parts={__instance.Parts?.Count ?? -1} mass={__instance.TotalMass:F0}kg " +
                $"sit={__instance.Situation} bubble={(__instance.PhysicsBubble != null)}");
            LogDecouplerRoster(__instance, "SENDER");

            if (TryAddress(__instance, splitConnector, out byte kind, out int index))
            {
                __state.Found = true;
                __state.Kind = kind;
                __state.Index = index;
                __state.Uid = UidOf(__instance);
            }
            else
            {
                Log($"SPLIT NOT SENT for {__instance.Id}: connector is in neither Decouplers nor DockingPorts");
            }
        }

        public static void SplitPostfix(Vehicle __instance, double splitImpulse,
                                        Vehicle? __result, SplitState __state)
        {
            if (_replaying || __result == null || __state == null || !__state.Found) return;

            try
            {
                // Derive the produced vessel's uid from its local name, preserving any MP| prefix.
                string newUid = UidOf(__result);
                bool producedIsTheirs = VesselIdentity.IsRemoteName(__result.Id);

                Log($"SPLIT DONE (sender): {__state.Uid} -> {newUid} via " +
                    $"{(__state.Kind == VesselStructureMessage.CONNECTOR_DECOUPLER ? "decoupler" : "port")}" +
                    $"#{__state.Index} impulse={splitImpulse:F0}");
                Log($"  parent after : {__instance.Id} parts={__instance.Parts?.Count ?? -1} " +
                    $"mass={__instance.TotalMass:F0}kg sit={__instance.Situation}");
                Log($"  produced     : {__result.Id} parts={__result.Parts?.Count ?? -1} " +
                    $"mass={__result.TotalMass:F0}kg sit={__result.Situation} " +
                    $"bubble={(__result.PhysicsBubble != null)} " +
                    $"controlled={ReferenceEquals(Program.ControlledVehicle, __result)}");
                Log($"  still flying : {Program.ControlledVehicle?.Id ?? "(none)"}");
                LogGeometry("undock-sender", __instance, __result);

                if (producedIsTheirs)
                {
                    // Hand the produced craft back to the renderer to be driven by its owner's network state.
                    VesselIdentity.TryParseUid(newUid, out string creator, out _);
                    Log($"UNDOCK: {__result.Id} is {creator}'s again - returning it to them");
                    OnRemoteVesselProduced?.Invoke(newUid, __result, creator);

                    // Arm the post-input control correction with the main viewport's camera.
                    Camera mainCamera = Program.MainViewport.GetCamera();
                    _undockKeep = __instance;
                    _undockReject = __result;
                    _undockCamera = mainCamera;
                    _undockWasFollowingKeep = ReferenceEquals(mainCamera.Following, __instance);
                }
                else
                {
                    OnVesselCreated?.Invoke(__result.Id);
                }

                OnProduced?.Invoke(new VesselStructureMessage
                {
                    Action = VesselStructureMessage.ACTION_SPLIT,
                    PrimaryUid = __state.Uid,
                    PrimaryConnectorIndex = __state.Index,
                    ConnectorKind = __state.Kind,
                    SplitImpulse = splitImpulse,
                    NewVesselUid = newUid,
                    SequenceNumber = ++_sequence
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR announcing split of {__instance?.Id}: {ex.Message}");
            }
        }

        public sealed class MergeState
        {
            public bool Found;
            public int SurvivorPort;
            public int ConsumedPort;
            public string SurvivorUid = string.Empty;
            public string ConsumedUid = string.Empty;
            public string ConsumedLocalName = string.Empty;
        }

        public static void MergePrefix(Vehicle __instance, Part.Connector thisConnector,
                                       Vehicle otherVehicle, Part.Connector otherConnector,
                                       out MergeState __state)
        {
            __state = new MergeState();
            if (_replaying || __instance == null || otherVehicle == null) return;

            // Announce only if one of the two vessels was created locally.
            if (VesselIdentity.IsRemoteName(__instance.Id) &&
                VesselIdentity.IsRemoteName(otherVehicle.Id))
                return;

            if (TryAddressPort(__instance, thisConnector, out int survivorPort) &&
                TryAddressPort(otherVehicle, otherConnector, out int consumedPort))
            {
                __state.Found = true;
                __state.SurvivorPort = survivorPort;
                __state.ConsumedPort = consumedPort;
                __state.SurvivorUid = UidOf(__instance);
                __state.ConsumedUid = UidOf(otherVehicle);
                __state.ConsumedLocalName = otherVehicle.Id;
                LogGeometry("dock-sender", __instance, otherVehicle);
            }
            else
            {
                Log($"DOCK NOT SENT for {__instance.Id}: docking port not found on one side");
            }
        }

        public static void MergePostfix(bool __result, MergeState __state)
        {
            if (_replaying || !__result || __state == null || !__state.Found) return;

            try
            {
                Log($"DOCK {__state.ConsumedUid} (port #{__state.ConsumedPort}) -> " +
                    $"{__state.SurvivorUid} (port #{__state.SurvivorPort})");

                OnVesselConsumed?.Invoke(__state.ConsumedLocalName);
                OnProduced?.Invoke(new VesselStructureMessage
                {
                    Action = VesselStructureMessage.ACTION_DOCK,
                    PrimaryUid = __state.SurvivorUid,
                    PrimaryConnectorIndex = __state.SurvivorPort,
                    SecondaryUid = __state.ConsumedUid,
                    SecondaryConnectorIndex = __state.ConsumedPort,
                    SequenceNumber = ++_sequence
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR announcing dock: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------- inbound

        private static readonly System.Collections.Generic.Queue<VesselStructureMessage> _pending = new();

        /// <summary>Uids a queued split is about to produce.</summary>
        private static readonly System.Collections.Generic.HashSet<string> _awaitingSplit = new();

        public static bool IsAwaitingSplit(string uid)
        {
            lock (_pending) return _awaitingSplit.Contains(uid);
        }

        /// <summary>Returns whether any queued structure event names this vessel, in any role.</summary>
        public static bool HasPendingStructureFor(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            lock (_pending)
            {
                foreach (VesselStructureMessage m in _pending)
                {
                    if (uid == m.PrimaryUid || uid == m.SecondaryUid || uid == m.NewVesselUid)
                        return true;
                }
            }
            return false;
        }

        public static void Apply(VesselStructureMessage msg)
        {
            if (msg.PlayerName == LocalPlayerName) return;   // our own, relayed back

            lock (_pending) _arrivedAt[msg.SequenceNumber] = DateTime.UtcNow;
            Log($"QUEUED (arrived in Network.Tick): {(msg.Action == 0 ? "SPLIT" : "DOCK")} " +
                $"{msg.PrimaryUid} seq={msg.SequenceNumber} - will drain at InputEvents.ApplyInputEvents");

            lock (_pending)
            {
                _pending.Enqueue(msg);
                if (msg.Action == VesselStructureMessage.ACTION_SPLIT &&
                    !string.IsNullOrEmpty(msg.NewVesselUid))
                {
                    _awaitingSplit.Add(msg.NewVesselUid);
                }
            }
        }

        /// <summary>Drains queued structure changes and raises OnSafeWindow.</summary>
        public static void Drain()
        {
            try { DrainQueue(); }
            finally
            {
                try { OnSafeWindow?.Invoke(); }
                catch (Exception ex) { Log($"ERROR in safe-window work: {ex.Message}"); }
            }
        }

        /// <summary>Replays deferred to a later safe window.</summary>
        private static readonly System.Collections.Generic.List<VesselStructureMessage> _deferred = new();

        private static void DrainQueue()
        {
            _deferredThisPass = false;

            // Re-queue anything deferred on the previous pass.
            lock (_pending)
            {
                if (_deferred.Count > 0)
                {
                    foreach (VesselStructureMessage d in _deferred) _pending.Enqueue(d);
                    _deferred.Clear();
                }
            }

            while (true)
            {
                VesselStructureMessage msg;
                lock (_pending)
                {
                    if (_pending.Count == 0) return;
                    msg = _pending.Dequeue();
                }

                try
                {
                    if (msg.Action == VesselStructureMessage.ACTION_SPLIT) ApplySplit(msg);
                    else ApplyDock(msg);
                }
                catch (Exception ex)
                {
                    Log($"ERROR draining structure event: {ex.Message}");
                }
                finally
                {
                    lock (_pending) _awaitingSplit.Remove(msg.NewVesselUid);
                }
            }
        }

        private static void ApplySplit(VesselStructureMessage msg)
        {
            try
            {
                double queuedMs = 0;
                lock (_pending)
                {
                    if (_arrivedAt.TryGetValue(msg.SequenceNumber, out DateTime arrived))
                    {
                        queuedMs = (DateTime.UtcNow - arrived).TotalMilliseconds;
                        _arrivedAt.Remove(msg.SequenceNumber);
                    }
                }

                Log($"=== STAGE BEGIN (receiver) === waited {queuedMs:F1}ms in the queue; from={msg.PlayerName} " +
                    $"{msg.PrimaryUid} -> {msg.NewVesselUid} " +
                    $"connector={(msg.ConnectorKind == VesselStructureMessage.CONNECTOR_DECOUPLER ? "decoupler" : "port")}" +
                    $"#{msg.PrimaryConnectorIndex} impulse={msg.SplitImpulse:F0} seq={msg.SequenceNumber}");
                Vehicle? vessel = Resolve(msg.PrimaryUid);
                if (vessel == null)
                {
                    Log($"SPLIT IGNORED: no local copy of {msg.PrimaryUid}");
                    return;
                }

                // Defer the split until the engine has given the vessel a physics bubble.
                if (vessel.PhysicsBubble == null)
                {
                    if (msg.BubbleWaitFrames > 8)
                    {
                        VehiclePatches.BubbleGrantFor = null;
                        Log($"SPLIT IGNORED: {vessel.Id} never received a physics bubble after " +
                            $"{msg.BubbleWaitFrames} frames");
                        return;
                    }

                    // Grant bubble membership for this vessel and retry on the next pass.
                    VehiclePatches.BubbleGrantFor = vessel;
                    msg.BubbleWaitFrames++;
                    _deferredThisPass = true;
                    lock (_pending) _deferred.Add(msg);
                    ModLogger.LogThrottled(LogName, $"BUBBLEWAIT_{msg.PrimaryUid}",
                        $"Waiting for {vessel.Id} to be given a physics bubble before splitting it " +
                        $"(window {msg.BubbleWaitFrames})");
                    return;
                }

                Log($"  resolved     : {vessel.Id} parts={vessel.Parts?.Count ?? -1} " +
                    $"mass={vessel.TotalMass:F0}kg sit={vessel.Situation} " +
                    $"bubble={(vessel.PhysicsBubble != null)}");
                LogDecouplerRoster(vessel, "RECEIVER");

                Part.Connector? connector = ConnectorAt(vessel, msg.ConnectorKind, msg.PrimaryConnectorIndex);
                if (connector == null)
                {
                    Log($"SPLIT IGNORED: connector #{msg.PrimaryConnectorIndex} " +
                        $"(kind {msg.ConnectorKind}) absent on {vessel.Id}");
                    return;
                }

                if (connector.Connection == null)
                {
                    // Skip: the connector is already detached.
                    Log($"SPLIT SKIPPED: connector #{msg.PrimaryConnectorIndex} on {vessel.Id} already detached");
                    return;
                }

                // Build the local name for the separated vessel from the sender's uid.
                string newLocalName = VesselIdentity.LocalNameFor(msg.NewVesselUid, LocalPlayerName);

                // Remove any vessel already holding the target name so the real split can produce it.
                var existing = Universe.CurrentSystem?.Get(newLocalName) as Vehicle;
                if (existing != null)
                {
                    Log($"SPLIT: {newLocalName} already present - built by design sync ahead of us; " +
                        $"removing it so the real split can produce it");

                    OnVesselReplaced?.Invoke(newLocalName);
                    // Release targets pointing at the vessel before deregistering it.
                    RemoteVehicleRenderer.ReleaseTargetsOn(existing);
                    Universe.CurrentSystem?.Deregister(existing);
                    existing.Dispose();
                }

                _replaying = true;
                try
                {
                    Vehicle? produced = vessel.Split(connector, msg.SplitImpulse, out PoseChange _, newLocalName);
                    if (produced == null)
                    {
                        Log($"SPLIT FAILED: Vehicle.Split returned null for {vessel.Id}");
                        return;
                    }

                    if (!string.Equals(produced.Id, newLocalName, StringComparison.Ordinal))
                        Log($"SPLIT WARNING: id mismatch, got {produced.Id} expected {newLocalName}");

                    Log($"SPLIT DONE (receiver): {vessel.Id} -> {produced.Id}");
                    Log($"  parent after : {vessel.Id} parts={vessel.Parts?.Count ?? -1} " +
                        $"mass={vessel.TotalMass:F0}kg");
                    Log($"  produced     : {produced.Id} parts={produced.Parts?.Count ?? -1} " +
                        $"mass={produced.TotalMass:F0}kg bubble={(produced.PhysicsBubble != null)}");
                    LogGeometry("undock-receiver", vessel, produced);

                    // Send a remote-named product to the renderer; keep a locally named one as our own.
                    if (VesselIdentity.IsRemoteName(produced.Id))
                    {
                        OnRemoteVesselProduced?.Invoke(msg.NewVesselUid, produced, msg.PlayerName);
                    }
                    else
                    {
                        Log($"UNDOCK: {produced.Id} is ours again - resuming publishing it");
                        OnVesselCreated?.Invoke(produced.Id);
                    }

                    // Revoke the grant and remove both remote-named halves from their bubbles.
                    VehiclePatches.BubbleGrantFor = null;
                    if (VesselIdentity.IsRemoteName(vessel.Id) && vessel.PhysicsBubble != null)
                        vessel.RemoveFromBubble(vessel.PhysicsBubble);
                    if (VesselIdentity.IsRemoteName(produced.Id) && produced.PhysicsBubble != null)
                        produced.RemoveFromBubble(produced.PhysicsBubble);

                    // Move the camera and controls onto the produced vessel when it is ours.
                    if (!VesselIdentity.IsRemoteName(produced.Id))
                    {
                        // Use the main viewport's camera, not the hovered one.
                        Camera camera = Program.MainViewport.GetCamera();
                        bool wasFollowingParent = ReferenceEquals(camera.Following, vessel);

                        // Take control only if the player is on the combined vessel, on nothing, or on a disposed craft.
                        bool shouldTakeControl =
                            ReferenceEquals(Program.ControlledVehicle, vessel) ||
                            Program.ControlledVehicle == null ||
                            Program.ControlledVehicle.IsDisposed;

                        if (wasFollowingParent)
                            camera.SetFollow(produced, camera.TidalLocking, changeControl: false);

                        if (shouldTakeControl)
                        {
                            Program.ControlledVehicle = produced;
                            Log($"UNDOCK: control returned to {produced.Id}");
                        }
                        else
                        {
                            Log($"UNDOCK: {produced.Id} is ours again, but the player is " +
                                $"flying {Program.ControlledVehicle?.Id ?? "(none)"} - leaving them there");
                        }
                    }

                    // Watch both halves render for the next few seconds.
                    WatchRender(vessel.Id);
                    WatchRender(produced.Id);
                    Log("=== STAGE END (receiver) === watching both vessels render for 3s");
                }
                finally
                {
                    _replaying = false;
                    if (!_deferredThisPass) VehiclePatches.BubbleGrantFor = null;
                }
            }
            catch (Exception ex)
            {
                if (!_deferredThisPass) VehiclePatches.BubbleGrantFor = null;
                Log($"ERROR applying split of {msg.PrimaryUid}: {ex.Message}");
                _replaying = false;
            }
        }

        private static void ApplyDock(VesselStructureMessage msg)
        {
            try
            {
                Vehicle? survivor = Resolve(msg.PrimaryUid);
                Vehicle? consumed = Resolve(msg.SecondaryUid);

                if (survivor == null || consumed == null)
                {
                    Log($"DOCK IGNORED: {msg.PrimaryUid} {(survivor == null ? "ABSENT" : "ok")}, " +
                        $"{msg.SecondaryUid} {(consumed == null ? "ABSENT" : "ok")}");
                    return;
                }

                DockingPort? survivorPort = PortAt(survivor, msg.PrimaryConnectorIndex);
                DockingPort? consumedPort = PortAt(consumed, msg.SecondaryConnectorIndex);

                if (survivorPort == null || consumedPort == null)
                {
                    Log($"DOCK IGNORED: port index out of range " +
                        $"({msg.PrimaryConnectorIndex}/{msg.SecondaryConnectorIndex})");
                    return;
                }

                if (survivorPort.Docked || consumedPort.Docked)
                {
                    Log("DOCK SKIPPED: a port is already docked - already applied");
                    return;
                }

                string consumedName = consumed.Id;
                LogGeometry("dock-receiver", survivor, consumed);

                // Record what the main viewport's camera is following and what is controlled before the merge.
                Camera camera = Program.MainViewport.GetCamera();
                bool wasFollowingConsumed = ReferenceEquals(camera.Following, consumed);
                bool wasFollowingEither = wasFollowingConsumed || ReferenceEquals(camera.Following, survivor);
                bool wasControllingEither = ReferenceEquals(Program.ControlledVehicle, consumed) ||
                                            ReferenceEquals(Program.ControlledVehicle, survivor);

                _replaying = true;
                try
                {
                    // Dock through the consumed vessel's port; the vessel passed as otherVehicle survives.
                    Vehicle? result = consumedPort.Dock(consumed, survivor, survivorPort, out PoseChange consumedToCombined);
                    if (result == null)
                    {
                        Log($"DOCK FAILED: Dock returned null for {consumedName} -> {survivor.Id}");
                        return;
                    }

                    OnVesselConsumed?.Invoke(consumedName);

                    if (wasFollowingEither)
                    {
                        camera.SetFollow(result, tidalLocking: true, changeControl: false);
                        if (wasFollowingConsumed)
                            Program.GetOrbitController().SetPoseChange(in consumedToCombined);
                    }

                    if (wasControllingEither)
                    {
                        if (VesselIdentity.IsRemoteName(result.Id))
                        {
                            // Release the controls rather than hand the player another player's craft.
                            Program.ControlledVehicle = null;
                            Log($"DOCK: {result.Id} belongs to another player - controls " +
                                $"released, ownership and camera unchanged");
                        }
                        else
                        {
                            Program.ControlledVehicle = result;
                            Log($"DOCK: control moved to {result.Id}");
                        }
                    }

                    Log($"DOCK APPLIED: {consumedName} consumed into {result.Id}");
                }
                finally { _replaying = false; }
            }
            catch (Exception ex)
            {
                Log($"ERROR applying dock: {ex.Message}");
                _replaying = false;
            }
        }
    }
}
