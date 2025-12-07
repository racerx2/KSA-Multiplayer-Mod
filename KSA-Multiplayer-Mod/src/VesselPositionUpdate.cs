using System;
using Brutal.Numerics;
using KSA;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Holds a single position update with all state needed for interpolation.
    /// Modeled after LMP's VesselPositionUpdate but adapted for KSA.
    /// 
    /// KSA Situations (for reference):
    /// - Freefall (0)    : In space/orbit, ON RAILS (Kepler) - uses CCI
    /// - Maneuvering (1) : With thrust, OFF RAILS (physics) - uses CCI
    /// - Rolling (2)     : On terrain, moving, OFF RAILS - uses CCF
    /// - Landed (3)      : On terrain, stationary, ON RAILS - uses CCF
    /// - Sailing (4)     : On ocean, moving, OFF RAILS - uses CCF
    /// - Floating (5)    : On ocean, stationary, ON RAILS - uses CCF
    /// 
    /// Surface situations (2-5) use CCF coordinates which rotate with the planet.
    /// We interpolate in CCF space and convert to CCI each frame.
    /// </summary>
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

        #endregion

        #region Interpolation Fields

        private double MaxInterpolationDuration => 2.0;
        public double TimeDifference { get; private set; }
        public double ExtraInterpolationTime { get; private set; }
        public bool InterpolationFinished => Target == null || CurrentFrame >= NumFrames;
        
        public double InterpolationDuration => Target == null ? 0 : 
            Math.Max(0, Math.Min(Target.GameTimeStamp - GameTimeStamp + ExtraInterpolationTime, MaxInterpolationDuration));
        
        public float LerpPercentage => NumFrames > 0 ? Math.Clamp(CurrentFrame / NumFrames, 0f, 1f) : 1f;
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
            VehicleKey = $"{msg.OwnerPlayerName}_{msg.VehicleId}";
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
            Situation = msg.Situation;
            PingSec = 0;
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
            KsaOrbit = other.KsaOrbit;
        }

        #endregion

        #region Main Methods

        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>
        /// Apply interpolated position to vehicle.
        /// Called every frame from RemoteVehicleRenderer.Update()
        /// </summary>
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

        /// <summary>
        /// Core update logic - handles dequeuing targets and applying position
        /// </summary>
        private void UpdateVesselWithPositionData(SubspaceManager subspaceManager)
        {
            if (Vessel == null) return;
            
            Celestial? parent = GetParentBody();
            if (parent == null) return;
            
            // If interpolation finished, try to get next target from queue
            if (InterpolationFinished)
            {
                var queue = PositionUpdateQueue.GetQueue(VehicleKey);
                if (queue != null && queue.TryDequeue(out var nextTarget) && nextTarget != null)
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
                    
                    CurrentFrame = 0;
                    
                    if (Target != null)
                    {
                        Target.CopyFrom(nextTarget);
                    }
                    else
                    {
                        Target = nextTarget;
                    }
                    
                    // Detect situation TYPE change (orbital ↔ surface)
                    // If transitioning from orbital (CCI) to surface (CCF), snap CCF coordinates
                    // because we had no valid CCF "from" data - it was (0,0,0)
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

        /// <summary>
        /// Check if situation is a surface contact situation (uses CCF coordinates)
        /// </summary>
        private static bool IsSurfaceSituation(byte situation)
        {
            // Rolling (2), Landed (3), Sailing (4), Floating (5)
            return situation >= 2;
        }

        /// <summary>
        /// Check if situation is on-rails (stationary)
        /// </summary>
        private static bool IsOnRails(byte situation)
        {
            // Freefall (0), Landed (3), Floating (5) are ON RAILS
            return situation == 0 || situation == 3 || situation == 5;
        }

        /// <summary>
        /// Apply position for surface situations (Landed, Floating, Rolling, Sailing).
        /// Interpolates in CCF space, converts to CCI using local time.
        /// </summary>
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
            double localTime = Universe.GetElapsedSimTime().Seconds();
            SimTime simTime = new SimTime(localTime);
            
            doubleQuat ccf2Cci = parent.GetCcf2Cci(simTime);
            double angularVel = parent.GetAngularVelocity();
            double3 omega = new double3(0, 0, angularVel);
            
            // Transform position and velocity from CCF to CCI
            double3 positionCci = lerpedPosCcf.Transform(ccf2Cci);
            double3 rotationalVel = double3.Cross(omega, positionCci);
            double3 velocityCci = lerpedVelCcf.Transform(ccf2Cci) + rotationalVel;
            
            // Create orbit from the converted CCI coordinates
            Orbit newOrbit = Orbit.CreateFromStateCci(parent, simTime, positionCci, velocityCci, Vessel.OrbitColor);
            var flightPlan = new FlightPlan(newOrbit, (uint)Vessel.Id.GetHashCode());
            Vessel.SetFlightPlan(flightPlan);
            
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

        /// <summary>
        /// Apply position for orbital situations (Freefall, Maneuvering).
        /// Uses orbit interpolation - query orbits at local time.
        /// </summary>
        private void ApplyOrbitalPosition(Celestial parent)
        {
            if (Vessel == null || KsaOrbit == null) return;
            
            double localTime = Universe.GetElapsedSimTime().Seconds();
            SimTime simTime = new SimTime(localTime);
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
            Orbit newOrbit = Orbit.CreateFromStateCci(parent, simTime, lerpedPos, lerpedVel, Vessel.OrbitColor);
            var flightPlan = new FlightPlan(newOrbit, (uint)Vessel.Id.GetHashCode());
            Vessel.SetFlightPlan(flightPlan);
            
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

        /// <summary>
        /// Initialize KSA orbits from CCI state vectors (for orbital situations only)
        /// </summary>
        private void InitializeOrbits(Celestial parent)
        {
            SimTime currentTime = new SimTime(GameTimeStamp);
            KsaOrbit = Orbit.CreateFromStateCci(parent, currentTime, PositionCci, VelocityCci, 
                Vessel?.OrbitColor ?? parent.OrbitColor);
            
            if (Target != null)
            {
                SimTime targetTime = new SimTime(Target.GameTimeStamp);
                Target.KsaOrbit = Orbit.CreateFromStateCci(parent, targetTime, Target.PositionCci, Target.VelocityCci,
                    Vessel?.OrbitColor ?? parent.OrbitColor);
            }
        }

        /// <summary>
        /// Apply rocket thrust visuals
        /// </summary>
        private void ApplyRocketThrusts()
        {
            if (Vessel?.Parts?.RocketNozzles?.States == null) return;
            
            float[] thrusts = Target?.RocketThrusts ?? RocketThrusts;
            if (thrusts.Length == 0) return;
            
            var nozzleStates = Vessel.Parts.RocketNozzles.States;
            int count = Math.Min(thrusts.Length, nozzleStates.Count);
            
            for (int i = 0; i < count; i++)
            {
                var state = nozzleStates[i];
                state.AverageThrottle = thrusts[i];
                state.Throttle = thrusts[i];
                nozzleStates[i] = state;
            }
        }

        /// <summary>
        /// Adjust interpolation timing to catch up or slow down.
        /// </summary>
        public void AdjustExtraInterpolationTimes(SubspaceManager subspaceManager)
        {
            double localTime = Universe.GetElapsedSimTime().Seconds();
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
