using System;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Logs the docking camera readout values and the target vessel's state.</summary>
    public static class DockingReadout
    {
        private const string LogName = "DockReadout";

        private static long _drawCall;

        private static AccessTools.FieldRef<DockingPort, bool>? _cameraEnabledRef;

        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                _cameraEnabledRef = AccessTools.FieldRefAccess<DockingPort, bool>("_cameraEnabled");
            }
            catch (Exception ex)
            {
                ModLogger.Log(LogName, $"Cannot reach DockingPort._cameraEnabled ({ex.Message}) - " +
                                       "readout diagnostic disabled");
                return;
            }

            var onDrawUi = AccessTools.Method(typeof(DockingPort), "OnDrawUi");
            if (onDrawUi == null)
            {
                ModLogger.Log(LogName, "DockingPort.OnDrawUi not found - readout diagnostic disabled");
                return;
            }

            // Patch OnDrawUi with a read-only postfix.
            harmony.Patch(onDrawUi,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(DockingReadout), nameof(OnDrawUiPostfix))));
            ModLogger.Log(LogName, "Patched DockingPort.OnDrawUi (postfix, read-only) - " +
                                   "recording what the panel was handed, changing nothing");
        }

        /// <summary>Returns the vessel's target vehicle.</summary>
        private static Vehicle? TargetVehicle(Vehicle? vehicle) => vehicle?.Target as Vehicle;

        private static DockingPort? TargetDockingPort(Vehicle? vehicle)
        {
            if (vehicle?.TargetPart == null) return null;
            Span<DockingPort> ports = vehicle.TargetPart.SubtreeModules.Get<DockingPort>();
            return ports.Length > 0 ? ports[0] : null;
        }

        public static void OnDrawUiPostfix(DockingPort __instance, Vehicle? vehicle, Viewport inViewport)
        {
            // Opt-in, and checked before any work: this runs on every draw call
            // of the docking camera panel. The patch stays installed either way
            // so the setting takes effect without restarting the game.
            if (!MultiplayerSettings.Current.LogDockingReadout) return;

            try
            {
                // Only record when the panel drew.
                if (inViewport.Index == 0) return;
                if (vehicle == null || vehicle.IsDisposed) return;
                if (_cameraEnabledRef == null || !_cameraEnabledRef(__instance)) return;

                Vehicle? target = TargetVehicle(vehicle);
                DockingPort? targetPort = TargetDockingPort(vehicle);
                if (target == null || targetPort == null || target.IsDisposed) return;

                // Compute the port-to-port distance the panel printed.
                double3 ourPos = vehicle.GetPositionCce();
                double3 targetPos = target.GetPositionCce();
                double3 ourPort = ourPos +
                    (__instance.Connector.PositionVehicleAsmb - vehicle.CenterOfMassAsmb).Transform(vehicle.Body2Cce);
                double3 theirPort = targetPos +
                    (targetPort.Connector.PositionVehicleAsmb - target.CenterOfMassAsmb).Transform(target.Body2Cce);

                double distance = (ourPort - theirPort).Length();

                // Compute closing velocity along the port-to-port line of sight.
                double3 relativeVelocity = target.GetVelocityCce() - vehicle.GetVelocityCce();
                double3 lineOfSight = (theirPort - ourPort).Normalized();
                double closing = double3.Dot(relativeVelocity, lineOfSight);

                _drawCall++;
                ModLogger.Log(LogName,
                    $"DRAW#{_drawCall} vp={inViewport.Index} dist={distance:F4}m cvel={closing:F4}m/s " +
                    $"tgt={target.Id} tgtCce=({targetPos.X:F2},{targetPos.Y:F2},{targetPos.Z:F2}) " +
                    $"ourCce=({ourPos.X:F2},{ourPos.Y:F2},{ourPos.Z:F2}) " +
                    $"tgtSit={target.Situation} onRails={target.Situation.IsOnRails()} " +
                    $"tgtBubble={(target.PhysicsBubble != null ? target.PhysicsBubble.NumVehicles.ToString() : "none")} " +
                    $"ourBubble={(vehicle.PhysicsBubble != null ? vehicle.PhysicsBubble.NumVehicles.ToString() : "none")}");
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottled(LogName, "READOUT_DIAG_ERR",
                    $"Readout diagnostic failed: {ex.Message}");
            }
        }
    }
}
