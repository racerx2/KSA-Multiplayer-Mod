using System.Collections.Generic;
using KSA;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Hides local vessels for the duration of a multiplayer session and restores them afterwards.</summary>
    public sealed class SessionUniverseManager
    {
        public static VehicleTemplate? ProxyTemplate { get; private set; }
        private readonly List<Vehicle> _hiddenLocalVehicles = new();
        private bool _active;

        public void Begin()
        {
            if (_active || Universe.CurrentSystem == null)
                return;

            Vehicle? controlledVehicle = Program.ControlledVehicle;
            ProxyTemplate = controlledVehicle?.BodyTemplate as VehicleTemplate;
            var toHide = new List<Vehicle>();
            foreach (Astronomical astronomical in Universe.CurrentSystem.All.AsSpan())
            {
                if (astronomical is Vehicle vehicle &&
                    vehicle != controlledVehicle &&
                    !VehiclePatches.IsRemoteVehicle(vehicle))
                {
                    ProxyTemplate ??= vehicle.BodyTemplate as VehicleTemplate;
                    toHide.Add(vehicle);
                }
            }

            foreach (Vehicle vehicle in toHide)
            {
                Universe.CurrentSystem.Deregister(vehicle);
                _hiddenLocalVehicles.Add(vehicle);
            }

            _active = true;
            Universe.CurrentSystem.UpdatePerFrameData();
            ModLogger.LogAlways("Vehicles",
                $"Multiplayer overlay started; temporarily hid {_hiddenLocalVehicles.Count} local vessel(s)");
        }

        public void Restore()
        {
            if (!_active)
                return;

            if (Universe.CurrentSystem != null)
            {
                foreach (Vehicle vehicle in _hiddenLocalVehicles)
                {
                    if (Universe.CurrentSystem.Get(vehicle.Id) == null)
                        Universe.CurrentSystem.Register(vehicle);
                }
                Universe.CurrentSystem.UpdatePerFrameData();
            }

            ModLogger.LogAlways("Vehicles",
                $"Multiplayer overlay ended; restored {_hiddenLocalVehicles.Count} local vessel(s)");
            _hiddenLocalVehicles.Clear();
            ProxyTemplate = null;
            _active = false;
        }
    }
}
