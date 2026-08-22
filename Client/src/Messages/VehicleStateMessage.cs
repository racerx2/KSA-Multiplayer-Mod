using System;
using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable(GenerateType.Object)]
    public partial class VehicleStateMessage : GameMessage
    {
        public const byte MESSAGE_ID = 200;
        
        public string VehicleId { get; set; } = string.Empty;
        public string OwnerPlayerName { get; set; } = string.Empty;

        /// <summary>Stable vessel identity in the form "{creator}|{localId}".</summary>
        public string VesselUid { get; set; } = string.Empty;

        /// <summary>True only for the vessel its owner is currently flying.</summary>
        public bool IsControlled { get; set; }

        public string ParentBodyId { get; set; } = string.Empty;
        
        /// <summary>The sender's local simulation time when this state was captured.</summary>
        public double StateTimeSeconds { get; set; }
        
        /// <summary>The server's simulation time when this state was captured.</summary>
        public double ServerTimeSeconds { get; set; }

        /// <summary>The sender's simulation speed when this state was sampled.</summary>
        public double SimulationSpeed { get; set; } = 1.0;
        
        // CCI position and velocity.
        public double PositionCciX { get; set; }
        public double PositionCciY { get; set; }
        public double PositionCciZ { get; set; }
        
        public double VelocityCciX { get; set; }
        public double VelocityCciY { get; set; }
        public double VelocityCciZ { get; set; }
        
        // CCF position and velocity.
        public double PositionCcfX { get; set; }
        public double PositionCcfY { get; set; }
        public double PositionCcfZ { get; set; }
        
        public double VelocityCcfX { get; set; }
        public double VelocityCcfY { get; set; }
        public double VelocityCcfZ { get; set; }
        
        // Physics frame: 0 = CCI, 1 = CCF.
        public byte PhysFrame { get; set; }
        
        // Orientation quaternion in the physics frame.
        public double OrientationX { get; set; }
        public double OrientationY { get; set; }
        public double OrientationZ { get; set; }
        public double OrientationW { get; set; }
        
        public double BodyRatesX { get; set; }
        public double BodyRatesY { get; set; }
        public double BodyRatesZ { get; set; }
        
        public bool EngineOn { get; set; }
        public float EngineThrottle { get; set; }
        public uint ThrusterFlags { get; set; }
        public bool IsManeuvering { get; set; }
        
        /// <summary>KSA Situation: 0=Freefall, 1=Maneuvering, 2=Rolling, 3=Landed, 4=Sailing, 5=Floating.</summary>
        public byte Situation { get; set; }
        
        /// <summary>KSA VehicleRegion: 0=Surface, 1=LowOrbit, 2=HighOrbit.</summary>
        public byte VehicleRegion { get; set; }
        
        public uint SequenceNumber { get; set; }
        
        // Per-rocket thrust levels.
        public float[] RocketThrusts { get; set; } = Array.Empty<float>();
        
        [MemoryPackConstructor]
        public VehicleStateMessage() : base((GameMessageId)MESSAGE_ID) { }
        
        public override void Execute() { }
    }
}
