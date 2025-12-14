using System;
using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>
    /// Server-side copy of VehicleStateMessage for deserialization.
    /// Only used to extract StateTimeSeconds for server time initialization.
    /// </summary>
    [MemoryPackable(GenerateType.Object)]
    public partial class VehicleStateMessage : GameMessage
    {
        public const byte MESSAGE_ID = 200;
        
        public string VehicleId { get; set; } = string.Empty;
        public string OwnerPlayerName { get; set; } = string.Empty;
        public string ParentBodyId { get; set; } = string.Empty;
        
        /// <summary>
        /// The sender's local simulation time when this state was captured.
        /// </summary>
        public double StateTimeSeconds { get; set; }
        
        /// <summary>
        /// The server's authoritative simulation time when this state was captured.
        /// </summary>
        public double ServerTimeSeconds { get; set; }
        
        // CCI coordinates (used for Freefall/Maneuvering)
        public double PositionCciX { get; set; }
        public double PositionCciY { get; set; }
        public double PositionCciZ { get; set; }
        
        public double VelocityCciX { get; set; }
        public double VelocityCciY { get; set; }
        public double VelocityCciZ { get; set; }
        
        // CCF coordinates (used for Landed/Floating/Rolling/Sailing)
        public double PositionCcfX { get; set; }
        public double PositionCcfY { get; set; }
        public double PositionCcfZ { get; set; }
        
        public double VelocityCcfX { get; set; }
        public double VelocityCcfY { get; set; }
        public double VelocityCcfZ { get; set; }
        
        // Physics frame: 0 = CCI, 1 = CCF
        public byte PhysFrame { get; set; }
        
        // Orientation (Body2Cci for orbital, Body2Ccf for surface)
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
        
        public byte Situation { get; set; }
        public byte VehicleRegion { get; set; }
        
        public uint SequenceNumber { get; set; }
        
        public float[] RocketThrusts { get; set; } = Array.Empty<float>();
        
        [MemoryPackConstructor]
        public VehicleStateMessage() : base((GameMessageId)MESSAGE_ID) { }
        
        public override void Execute() { }
    }
}
