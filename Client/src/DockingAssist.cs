using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Finds docking opportunities with remote vessels and commits them through KSA's input buffer.</summary>
    public static class DockingAssist
    {
        private const string LogName = "Structure";

        /// <summary>Maximum dot product between port axes for the ports to count as facing.</summary>
        private const double RequiredFacingDot = -0.5;

        /// <summary>Maximum port-to-port distance at which docking is allowed.</summary>
        private const double DockDistanceMeters = 2.0;

        /// <summary>Maximum port-to-port distance at which a pairing is reported.</summary>
        private const double AdvisoryRangeMeters = 500.0;

        public sealed class Candidate
        {
            public Vehicle LocalVessel = null!;
            public DockingPort LocalPort = null!;
            public Vehicle RemoteVessel = null!;
            public DockingPort RemotePort = null!;
            public string RemoteKey = string.Empty;

            /// <summary>Port-to-port separation in metres.</summary>
            public double Distance;

            /// <summary>Dot product of the two port axes.</summary>
            public double Facing;

            /// <summary>Lateral offset of the target port along our port's Y and Z axes, in metres.</summary>
            public double LateralY;
            public double LateralZ;

            public bool CloseEnough => Distance < DockDistanceMeters;
            public bool Aligned => Facing < RequiredFacingDot;
            public bool Ready => CloseEnough && Aligned;
        }

        /// <summary>The closest pairing found on the last update.</summary>
        public static Candidate? Nearest { get; private set; }

        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>Recomputes the nearest docking pairing.</summary>
        public static void Update(RemoteVehicleRenderer? renderer)
        {
            try
            {
                Nearest = FindNearest(renderer);
                LogReadoutChurn();
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottled(LogName, "DOCKASSIST_ERR", $"Docking search failed: {ex.Message}");
                Nearest = null;
            }
        }

        private static int _churnFrame;
        private static double _lastChurnDistance = double.NaN;

        /// <summary>Logs the docking readout values every frame while inside 100 m.</summary>
        /// <remarks>
        /// Per-frame by design: the point of the trace is to show how the
        /// distance and alignment move between frames, which a throttled sample
        /// cannot. That makes it far too loud to leave on, so it is opt-in
        /// through <see cref="MultiplayerSettings.LogDockingReadout"/>.
        /// </remarks>
        private static void LogReadoutChurn()
        {
            if (!MultiplayerSettings.Current.LogDockingReadout)
            {
                _lastChurnDistance = double.NaN;
                return;
            }

            Candidate? c = Nearest;
            if (c == null || c.Distance > 100.0)
            {
                _lastChurnDistance = double.NaN;
                return;
            }

            // Counts frames inside docking range.
            _churnFrame++;

            double step = double.IsNaN(_lastChurnDistance)
                ? 0.0
                : Math.Abs(c.Distance - _lastChurnDistance);
            _lastChurnDistance = c.Distance;

            // Classifies the controlled vessel's target as none, disposed, stale, or live.
            Vehicle? target = c.LocalVessel.Target as Vehicle;
            string tgt;
            if (target == null) tgt = "none";
            else if (target.IsDisposed) tgt = $"DISPOSED({target.Id})";
            else if (!ReferenceEquals(target, c.RemoteVessel)) tgt = $"STALE(target={target.Id} != nearest={c.RemoteVessel.Id})";
            else tgt = $"live({target.Id})";

            ModLogger.Log("DockReadout",
                $"f{_churnFrame} dist={c.Distance:F4}m step={step * 100.0:F3}cm " +
                $"facing={c.Facing:F4} y={c.LateralY * 100.0:F2}cm z={c.LateralZ * 100.0:F2}cm " +
                $"tgt={tgt}");
        }

        private static Candidate? FindNearest(RemoteVehicleRenderer? renderer)
        {
            if (renderer == null) return null;

            Vehicle? local = Program.ControlledVehicle;
            if (local == null || local.IsDisposed) return null;

            // Skips vessels that are not locally owned.
            if (VesselIdentity.IsRemoteName(local.Id)) return null;

            var localPorts = local.Parts?.DockingPorts;
            if (localPorts == null || localPorts.NumModules == 0) return null;

            string localParent = local.Parent?.Id ?? string.Empty;
            if (string.IsNullOrEmpty(localParent)) return null;

            Candidate? best = null;

            foreach (KeyValuePair<string, Vehicle> entry in renderer.RemoteVehicles)
            {
                Vehicle remote = entry.Value;
                if (remote == null || remote.IsDisposed) continue;

                // Skips remote vessels orbiting a different parent body.
                if ((remote.Parent?.Id ?? string.Empty) != localParent) continue;

                var remotePorts = remote.Parts?.DockingPorts;
                if (remotePorts == null || remotePorts.NumModules == 0) continue;

                for (int i = 0; i < localPorts.NumModules; i++)
                {
                    DockingPort? lp = localPorts[i];
                    if (lp == null || lp.Docked) continue;

                    double3 lPos = PortPositionCce(local, lp);
                    double3 lAxis = PortAxisCce(local, lp);

                    for (int j = 0; j < remotePorts.NumModules; j++)
                    {
                        DockingPort? rp = remotePorts[j];
                        if (rp == null || rp.Docked) continue;

                        double3 rPos = PortPositionCce(remote, rp);
                        double distance = (lPos - rPos).Length();
                        if (double.IsNaN(distance) || distance > AdvisoryRangeMeters) continue;
                        if (best != null && distance >= best.Distance) continue;

                        // Resolves the port-to-port vector onto our port's lateral axes.
                        double3 toTarget = rPos - lPos;
                        doubleQuat lRot = lp.Connector.Asmb2VehicleAsmb.Concatenate(local.Asmb2Cce);

                        best = new Candidate
                        {
                            LocalVessel = local,
                            LocalPort = lp,
                            RemoteVessel = remote,
                            RemotePort = rp,
                            RemoteKey = entry.Key,
                            Distance = distance,
                            Facing = double3.Dot(lAxis, PortAxisCce(remote, rp)),
                            LateralY = double3.Dot(toTarget, new double3(0.0, 1.0, 0.0).Transform(lRot)),
                            LateralZ = double3.Dot(toTarget, new double3(0.0, 0.0, 1.0).Transform(lRot))
                        };
                    }
                }
            }

            return best;
        }

        /// <summary>Returns a port's position in the parent body's centred frame.</summary>
        private static double3 PortPositionCce(Vehicle v, DockingPort port) =>
            v.GetPositionCce() +
            (port.Connector.PositionVehicleAsmb - v.CenterOfMassAsmb).Transform(v.Body2Cce);

        /// <summary>Returns the direction a port points.</summary>
        private static double3 PortAxisCce(Vehicle v, DockingPort port) =>
            new double3(1.0, 0.0, 0.0).Transform(port.Connector.Asmb2VehicleAsmb.Concatenate(v.Asmb2Cce));

        /// <summary>Returns the current candidate if the given port is either end of it.</summary>
        public static Candidate? CandidateFor(DockingPort port)
        {
            Candidate? c = Nearest;
            if (c == null || port == null) return null;
            return (ReferenceEquals(c.LocalPort, port) || ReferenceEquals(c.RemotePort, port)) ? c : null;
        }

        /// <summary>Appends a Dock entry to the stock port context menu.</summary>
        public static void ShowContextMenuPostfix(DockingPort __instance, ref bool __result)
        {
            try
            {
                if (__result) return;

                Candidate? c = CandidateFor(__instance);
                if (c == null || !c.Ready) return;

                string label = $"Dock to {c.RemoteVessel.Id} ({c.Distance:F2} m)###mpdock{__instance.InstanceId}";
                if (ImGui.MenuItem(label, string.Empty))
                {
                    if (Commit(c)) __result = true;
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottled(LogName, "DOCKMENU_ERR", $"Docking menu item failed: {ex.Message}");
            }
        }

        /// <summary>Queues a dock on KSA's docking input buffer, keeping the local vessel.</summary>
        public static bool Commit(Candidate candidate)
        {
            if (candidate == null) return false;

            if (!candidate.Ready)
            {
                Log($"DOCK REFUSED: {candidate.Distance:F2}m apart, facing {candidate.Facing:F2} - " +
                    $"needs under {DockDistanceMeters:F1}m and below {RequiredFacingDot:F1}");
                return false;
            }

            if (candidate.LocalVessel.IsDisposed || candidate.RemoteVessel.IsDisposed)
            {
                Log("DOCK REFUSED: one of the vessels has been destroyed");
                return false;
            }

            if (candidate.LocalPort.Docked || candidate.RemotePort.Docked)
            {
                Log("DOCK REFUSED: a port is already docked");
                return false;
            }

            Log($"DOCK REQUESTED: {candidate.RemoteVessel.Id} -> {candidate.LocalVessel.Id} " +
                $"at {candidate.Distance:F2}m, facing {candidate.Facing:F2}; " +
                "queued to InputEvents.VehicleDockingInputBuffer for the safe window");

            InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData
            {
                Vehicle = candidate.RemoteVessel,          // consumed
                DockingPort = candidate.RemotePort,
                NearbyVehicle = candidate.LocalVessel,     // survives
                NearbyDockingPort = candidate.LocalPort,
                Undock = false
            });

            return true;
        }
    }
}
