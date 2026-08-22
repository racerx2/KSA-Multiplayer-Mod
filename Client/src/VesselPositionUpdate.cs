using System;
using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Holds a single position update with all state needed for interpolation.</summary>
    public class VesselPositionUpdate
    {
        #region Fields

        public Vehicle? Vessel { get; set; }
        public VesselPositionUpdate? Target { get; set; }
        public Orbit? KsaOrbit { get; set; }

        #endregion

        #region Message Fields

        public string VehicleKey { get; set; } = string.Empty;
        public string ParentBodyId { get; set; } = "Earth";
        
        // CCI coordinates (for orbital situations)
        public double3 PositionCci { get; set; }
        public double3 VelocityCci { get; set; }
        
        // CCF coordinates (for surface situations)
        public double3 PositionCcf { get; set; }
        public double3 VelocityCcf { get; set; }
        
        // Physics frame: 0=CCI, 1=CCF
        public byte PhysFrame { get; set; }
        
        // Orientation (Body2Cci for orbital, Body2Ccf for surface)
        public doubleQuat Orientation { get; set; } = doubleQuat.Identity;
        public double3 BodyRates { get; set; }
        public float[] RocketThrusts { get; set; } = Array.Empty<float>();
        
        /// <summary>Game time when this state was captured by sender</summary>
        public double GameTimeStamp { get; set; }
        
        /// <summary>KSA vehicle situation (0=Freefall, 1=Maneuvering, etc)</summary>
        public byte Situation { get; set; }
        
        /// <summary>Network latency for this update</summary>
        public float PingSec { get; set; }

        /// <summary>Monotonic local arrival time used for real-time interpolation.</summary>
        public double ArrivalTimeSeconds { get; set; }

        #endregion

        #region Interpolation Fields

        private const double MinInterpolationDuration = 0.04;
        private const double MaxInterpolationDuration = 0.12;
        private double _interpolationStartedAtSeconds;

        /// <summary>Whether this update's flight plan has been handed to the vessel yet.</summary>
        private bool _flightPlanApplied;
        public double TimeDifference { get; private set; }
        public double ExtraInterpolationTime { get; private set; }
        public bool InterpolationFinished => Target == null ||
            GetMonotonicSeconds() - _interpolationStartedAtSeconds >=
                InterpolationDuration;
        
        public double InterpolationDuration => Target == null ? 0 :
            Math.Clamp(Target.ArrivalTimeSeconds - ArrivalTimeSeconds,
                MinInterpolationDuration, MaxInterpolationDuration);
        
        public float LerpPercentage => Target == null ? 1f :
            (float)Math.Clamp(
                (GetMonotonicSeconds() - _interpolationStartedAtSeconds) /
                    InterpolationDuration,
                0, 1);
        public float CurrentFrame { get; set; }
        public int NumFrames => (int)(InterpolationDuration / FixedDeltaTime) + 1;
        
        private const double FixedDeltaTime = 0.02;
        public static float MessageOffsetSec(float pingSec) => Math.Clamp(pingSec * 2, 0.1f, 1.0f);
        
        private const string LogName = "Sync";

        #endregion

        #region Constructors

        public VesselPositionUpdate() { }

        public VesselPositionUpdate(VehicleStateMessage msg)
        {
            VehicleKey = VesselIdentity.UidFromWire(msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty);
            ParentBodyId = msg.ParentBodyId ?? "Earth";
            
            // CCI coordinates
            PositionCci = new double3(msg.PositionCciX, msg.PositionCciY, msg.PositionCciZ);
            VelocityCci = new double3(msg.VelocityCciX, msg.VelocityCciY, msg.VelocityCciZ);
            
            // CCF coordinates
            PositionCcf = new double3(msg.PositionCcfX, msg.PositionCcfY, msg.PositionCcfZ);
            VelocityCcf = new double3(msg.VelocityCcfX, msg.VelocityCcfY, msg.VelocityCcfZ);
            
            PhysFrame = msg.PhysFrame;
            Orientation = new doubleQuat(msg.OrientationX, msg.OrientationY, msg.OrientationZ, msg.OrientationW);
            BodyRates = new double3(msg.BodyRatesX, msg.BodyRatesY, msg.BodyRatesZ);
            RocketThrusts = msg.RocketThrusts ?? Array.Empty<float>();
            GameTimeStamp = msg.StateTimeSeconds;
            Situation = RemoteSituation(msg.Situation);
            PingSec = 0;
            ArrivalTimeSeconds = GetMonotonicSeconds();
        }

        public void CopyFrom(VesselPositionUpdate other)
        {
            VehicleKey = other.VehicleKey;
            ParentBodyId = other.ParentBodyId;
            PositionCci = other.PositionCci;
            VelocityCci = other.VelocityCci;
            PositionCcf = other.PositionCcf;
            VelocityCcf = other.VelocityCcf;
            PhysFrame = other.PhysFrame;
            Orientation = other.Orientation;
            BodyRates = other.BodyRates;
            RocketThrusts = other.RocketThrusts;
            GameTimeStamp = other.GameTimeStamp;
            Situation = other.Situation;
            PingSec = other.PingSec;
            ArrivalTimeSeconds = other.ArrivalTimeSeconds;
            KsaOrbit = other.KsaOrbit;
        }

        private void StartInterpolationNow()
        {
            _interpolationStartedAtSeconds = GetMonotonicSeconds();
            _flightPlanApplied = false;
            CurrentFrame = 0;
        }

        public static double GetMonotonicSeconds() =>
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        #endregion

        #region Main Methods

        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>Applies the interpolated position to the vehicle.</summary>
        public void ApplyInterpolatedUpdate(SubspaceManager subspaceManager)
        {
            try
            {
                UpdateVesselWithPositionData(subspaceManager);
            }
            finally
            {
                CurrentFrame++;
            }
        }

        /// <summary>Dequeues the next target and applies its position.</summary>
        private void UpdateVesselWithPositionData(SubspaceManager subspaceManager)
        {
            if (Vessel == null) return;

            // Stop updating a disposed vessel.
            if (Vessel.IsDisposed)
            {
                ModLogger.LogThrottled(LogName, $"DISPOSED_{VehicleKey}",
                    $"{VehicleKey} has been destroyed - no longer updating its state");
                return;
            }
            
            Celestial? parent = GetParentBody();
            if (parent == null) return;
            
            // If interpolation finished, try to get next target from queue
            if (InterpolationFinished)
            {
                var queue = PositionUpdateQueue.GetQueue(VehicleKey);
                if (queue != null &&
                    queue.TryDequeueLatest(out var nextTarget) &&
                    nextTarget != null)
                {
                    // Save old situation before copying
                    byte oldSituation = Situation;
                    
                    if (Target == null)
                    {
                        // First iteration - set ourselves slightly behind target
                        GameTimeStamp = nextTarget.GameTimeStamp - 0.1;
                        CopyFrom(nextTarget);
                    }
                    else
                    {
                        // Move current to previous target
                        CopyFrom(Target);
                    }
                    
                    if (Target != null)
                    {
                        Target.CopyFrom(nextTarget);
                    }
                    else
                    {
                        Target = nextTarget;
                    }
                    StartInterpolationNow();
                    
                    // Detect an orbital-to-surface transition.
                    bool wasOrbital = !IsSurfaceSituation(oldSituation);
                    bool nowSurface = IsSurfaceSituation(Target?.Situation ?? Situation);
                    
                    if (wasOrbital && nowSurface && Target != null)
                    {
                        // Snap to target CCF coordinates to avoid lerping from (0,0,0)
                        PositionCcf = Target.PositionCcf;
                        VelocityCcf = Target.VelocityCcf;
                        Orientation = Target.Orientation;
                        Log($"SNAP CCF: Orbital->Surface transition, snapping to CCF position");
                    }
                    
                    // Adjust interpolation timing
                    AdjustExtraInterpolationTimes(subspaceManager);
                    
                    // Initialize orbits for orbital situations
                    if (!IsSurfaceSituation(Target?.Situation ?? Situation))
                    {
                        InitializeOrbits(parent);
                    }
                }
            }
            
            if (Target == null) return;
            
            // Choose update method based on situation
            byte currentSituation = Target?.Situation ?? Situation;
            
            if (IsSurfaceSituation(currentSituation))
            {
                // Surface situations: interpolate in CCF, convert to CCI each frame
                ApplySurfacePosition(parent);
            }
            else
            {
                // Orbital situations: use orbit interpolation
                ApplyOrbitalPosition(parent);
            }
            
            // Apply rocket thrust visuals
            ApplyRocketThrusts();
        }

        /// <summary>Returns true when every component is finite.</summary>
        private static bool IsFinite(double3 v) =>
            !double.IsNaN(v.X) && !double.IsNaN(v.Y) && !double.IsNaN(v.Z) &&
            !double.IsInfinity(v.X) && !double.IsInfinity(v.Y) && !double.IsInfinity(v.Z);

        /// <summary>Refreshes a vessel's stored state vectors with a resolved true anomaly.</summary>
        private static bool ApplyStateVectors(Vehicle vessel, UniverseTime simTime,
            double3 positionCci, double3 velocityCci)
        {
            if (!IsFinite(positionCci) || !IsFinite(velocityCci))
            {
                ModLogger.LogThrottledAlways(LogName, $"NONFINITE_{vessel.Id}",
                    $"Refusing a non-finite state for {vessel.Id}: pos={positionCci}, vel={velocityCci}");
                return false;
            }

            TrueAnomaly trueAnomaly;
            try
            {
                trueAnomaly = vessel.Orbit.GetStateVectorsAt(simTime).TrueAnomaly;
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottledAlways(LogName, $"TA_THREW_{vessel.Id}",
                    $"Could not resolve a true anomaly for {vessel.Id}: {ex.Message}");
                return false;
            }

            if (trueAnomaly.IsNaN())
            {
                ModLogger.LogThrottledAlways(LogName, $"TA_NAN_{vessel.Id}",
                    $"Orbit for {vessel.Id} yields no true anomaly at T={simTime.Seconds():F3}s - " +
                    "leaving its stored state untouched rather than writing NaN into it");
                return false;
            }

            vessel.Orbit.UpdatePosition(new StateVectors(simTime, positionCci, velocityCci, trueAnomaly));
            return true;
        }

        /// <summary>Returns true for surface-contact situation values.</summary>
        private static bool IsSurfaceSituation(byte situation)
        {
            return situation >= 2;
        }

        // IsOnRails(byte) removed; use Situation.IsOnRails().

        /// <summary>Applies position for surface situations.</summary>
        private void ApplySurfacePosition(Celestial parent)
        {
            if (Vessel == null) return;
            
            float lerp = LerpPercentage;
            
            // Interpolate in CCF space (body-fixed)
            double3 targetPosCcf = Target?.PositionCcf ?? PositionCcf;
            double3 targetVelCcf = Target?.VelocityCcf ?? VelocityCcf;
            
            double3 lerpedPosCcf = Lerp(PositionCcf, targetPosCcf, lerp);
            double3 lerpedVelCcf = Lerp(VelocityCcf, targetVelCcf, lerp);
            
            // Convert CCF to CCI using LOCAL time (receiver's planet rotation)
            double localTime = Universe.GetElapsedTime().Seconds();
            UniverseTime simTime = new UniverseTime(localTime);
            
            doubleQuat ccf2Cci = parent.GetCcf2Cci(simTime);
            double angularVel = parent.GetAngularVelocity();
            double3 omega = new double3(0, 0, angularVel);
            
            // Transform position and velocity from CCF to CCI
            double3 positionCci = lerpedPosCcf.Transform(ccf2Cci);
            double3 rotationalVel = double3.Cross(omega, positionCci);
            double3 velocityCci = lerpedVelCcf.Transform(ccf2Cci) + rotationalVel;
            
            // Create orbit from the converted CCI coordinates
            if (!_flightPlanApplied)
            {
                // Reject a non-finite state.
                if (!IsFinite(positionCci) || !IsFinite(velocityCci))
                {
                    ModLogger.LogThrottledAlways(LogName, $"NONFINITE_PLAN_{VehicleKey}",
                        $"Refusing to build a surface orbit for {VehicleKey} from a non-finite state");
                    return;
                }
                Orbit newOrbit = Orbit.CreateFromStateCci(parent, simTime, positionCci, velocityCci, Vessel.OrbitColor);
                Vessel.SetFlightPlan(new FlightPlan(newOrbit, new KeyHash((uint)Vessel.Id.GetHashCode())));
                _flightPlanApplied = true;
            }
            else
            {
                // Refresh the existing orbit's state vectors.
                ApplyStateVectors(Vessel, simTime, positionCci, velocityCci);
            }
            
            // Interpolate orientation in CCF space
            doubleQuat targetOrientation = Target?.Orientation ?? Orientation;
            doubleQuat lerpedOrientationCcf = doubleQuat.Slerp(Orientation, targetOrientation, lerp);
            
            // Convert CCF orientation to CCE for display
            doubleQuat ccf2Cce = parent.GetCcf2Cce();
            doubleQuat body2Cce = doubleQuat.Concatenate(lerpedOrientationCcf, ccf2Cce);
            
            // Apply orientation
            var prop = typeof(Vehicle).GetProperty("Body2Cce");
            prop?.SetValue(Vessel, body2Cce);
            
            Vessel.UpdatePerFrameData();
        }

        /// <summary>Applies position for orbital situations.</summary>

        /// <summary>Situation values for Maneuvering and Freefall.</summary>
        private const byte SituationManeuvering = 0;
        private const byte SituationFreefall = 1;

        /// <summary>Maps a reported Maneuvering situation to Freefall for remote vessels.</summary>
        public static byte RemoteSituation(byte reported)
            => reported == SituationManeuvering ? SituationFreefall : reported;

        private void ApplyOrbitalPosition(Celestial parent)
        {
            if (Vessel == null || KsaOrbit == null) return;
            
            double localTime = Universe.GetElapsedTime().Seconds();
            UniverseTime simTime = new UniverseTime(localTime);
            float lerp = LerpPercentage;
            
            // Get current and target positions from their orbits at LOCAL time
            double3 currentPos = KsaOrbit.GetStateVectorsAt(simTime).PositionCci;
            double3 currentVel = KsaOrbit.GetStateVectorsAt(simTime).VelocityCci;
            
            double3 targetPos = currentPos;
            double3 targetVel = currentVel;
            
            if (Target?.KsaOrbit != null)
            {
                targetPos = Target.KsaOrbit.GetStateVectorsAt(simTime).PositionCci;
                targetVel = Target.KsaOrbit.GetStateVectorsAt(simTime).VelocityCci;
            }
            
            // Lerp between positions
            double3 lerpedPos = Lerp(currentPos, targetPos, lerp);
            double3 lerpedVel = Lerp(currentVel, targetVel, lerp);
            
            // Create new orbit at lerped position
            if (!_flightPlanApplied)
            {
                if (!IsFinite(lerpedPos) || !IsFinite(lerpedVel))
                {
                    ModLogger.LogThrottledAlways(LogName, $"NONFINITE_PLAN_{VehicleKey}",
                        $"Refusing to build an orbit for {VehicleKey} from a non-finite state");
                    return;
                }
                // Once per received update: give the vessel the authoritative orbit.
                Orbit newOrbit = Orbit.CreateFromStateCci(parent, simTime, lerpedPos, lerpedVel, Vessel.OrbitColor);
                Vessel.SetFlightPlan(new FlightPlan(newOrbit, new KeyHash((uint)Vessel.Id.GetHashCode())));
                _flightPlanApplied = true;
            }
            else
            {
                // Advance the existing orbit's state vectors.
                ApplyStateVectors(Vessel, simTime, lerpedPos, lerpedVel);
            }
            
            // Lerp orientation (in CCI for orbital)
            doubleQuat targetOrientation = Target?.Orientation ?? Orientation;
            doubleQuat lerpedOrientationCci = doubleQuat.Slerp(Orientation, targetOrientation, lerp);
            
            // Convert CCI orientation to CCE for display
            doubleQuat cci2Cce = parent.GetCci2Cce();
            doubleQuat body2Cce = doubleQuat.Concatenate(lerpedOrientationCci, cci2Cce);
            
            // Apply orientation
            var prop = typeof(Vehicle).GetProperty("Body2Cce");
            prop?.SetValue(Vessel, body2Cce);
            
            Vessel.UpdatePerFrameData();
        }

        /// <summary>Creates KSA orbits from CCI state vectors at the sender's timestamp.</summary>
        private void InitializeOrbits(Celestial parent)
        {
            UniverseTime currentTime = new UniverseTime(GameTimeStamp);
            KsaOrbit = Orbit.CreateFromStateCci(parent, currentTime, PositionCci, VelocityCci, 
                Vessel?.OrbitColor ?? parent.OrbitColor);
            
            if (Target != null)
            {
                UniverseTime targetTime = new UniverseTime(Target.GameTimeStamp);
                Target.KsaOrbit = Orbit.CreateFromStateCci(parent, targetTime, Target.PositionCci, Target.VelocityCci,
                    Vessel?.OrbitColor ?? parent.OrbitColor);
            }
        }

        /// <summary>Last thrust values applied per remote vehicle.</summary>
        public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Vehicle, float[]> LastAppliedThrusts = new();

        private void ApplyRocketThrusts()
        {
            if (Vessel?.Parts?.RocketNozzles == null) return;
            
            float[] thrusts = Target?.RocketThrusts ?? RocketThrusts;
            if (thrusts.Length == 0) return;

            // Record the thrust values for the render-time pass.
            LastAppliedThrusts.AddOrUpdate(Vessel, thrusts);
        }

        /// <summary>Writes transmitted nozzle thrust into the render-side state buffer.</summary>
        public static void ApplyThrustsTo(Vehicle vessel, float[] thrusts)
        {
            if (vessel?.Parts?.RocketNozzles == null || thrusts.Length == 0) return;
            
            var rocketNozzles = vessel.Parts.RocketNozzles;
            // Bound the loop by the states array length.
            int count = Math.Min(thrusts.Length, rocketNozzles.States.Length);
            
            for (int i = 0; i < count; i++)
            {
                var states = rocketNozzles.GetModuleAndAllMutableStatesForInitializationByIdx(i);
                states.State.ThrustFraction = thrusts[i];
                states.State.AverageThrustFraction = thrusts[i];

                // Gate the plume visuals on whether the nozzle is firing.
                states.State.DutyCycle = thrusts[i] > 0f ? 1f : 0f;
            }
        }

        /// <summary>Adjusts interpolation timing to catch up or slow down.</summary>
        public void AdjustExtraInterpolationTimes(SubspaceManager subspaceManager)
        {
            double localTime = Universe.GetElapsedTime().Seconds();
            double messageOffset = MessageOffsetSec(PingSec);
            
            TimeDifference = localTime - GameTimeStamp - messageOffset;
            
            if (TimeDifference > 0)
            {
                ExtraInterpolationTime = -GetInterpolationFixFactor();
            }
            else
            {
                ExtraInterpolationTime = GetInterpolationFixFactor();
            }
        }

        private double GetInterpolationFixFactor()
        {
            double errorInSeconds = Math.Abs(TimeDifference);
            double errorInFrames = errorInSeconds / FixedDeltaTime;
            
            if (errorInFrames < 1) return 0;
            if (errorInFrames <= 2) return FixedDeltaTime;
            if (errorInFrames <= 5) return FixedDeltaTime * 2;
            if (errorInSeconds <= 2.5) return FixedDeltaTime * errorInFrames / 2;
            
            return FixedDeltaTime * errorInFrames;
        }

        #endregion

        #region Helper Methods

        private Celestial? GetParentBody()
        {
            if (Universe.CurrentSystem == null) return null;
            
            Astronomical? parent = Universe.CurrentSystem.Get(ParentBodyId);
            if (parent == null)
                parent = Universe.CurrentSystem.Get("Earth");
            
            return parent as Celestial;
        }

        private static double3 Lerp(double3 a, double3 b, float t)
        {
            return new double3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }

        #endregion
    }
}
