using MemoryPack;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer.Messages
{
    [MemoryPackable(GenerateType.Object)]
    public partial class OrbitSyncMessage : GameMessage
    {
        public const byte MESSAGE_ID = 203;
        
        // Global simulation time (GameTime in XML)
        public double GameTimeSeconds { get; set; }
        
        // AnalyticState - orbital state (from Orbit.StateVectors)
        public double AnalyticTimeSeconds { get; set; }
        public double PositionCciX { get; set; }
        public double PositionCciY { get; set; }
        public double PositionCciZ { get; set; }
        public double VelocityCciX { get; set; }
        public double VelocityCciY { get; set; }
        public double VelocityCciZ { get; set; }
        // Body2Cce as XYZ radians (euler angles)
        public double Body2CceX { get; set; }
        public double Body2CceY { get; set; }
        public double Body2CceZ { get; set; }
        // BodyRates (angular velocity)
        public double BodyRatesX { get; set; }
        public double BodyRatesY { get; set; }
        public double BodyRatesZ { get; set; }
        
        // KinematicState - physics state
        public double KinematicTimeSeconds { get; set; }
        public byte Situation { get; set; }
        public byte PhysFrame { get; set; }
        public double PositionPhysX { get; set; }
        public double PositionPhysY { get; set; }
        public double PositionPhysZ { get; set; }
        public double VelocityPhysX { get; set; }
        public double VelocityPhysY { get; set; }
        public double VelocityPhysZ { get; set; }
        // Body2Phys as XYZ radians
        public double Body2PhysX { get; set; }
        public double Body2PhysY { get; set; }
        public double Body2PhysZ { get; set; }
        public double BodyRatesPhysX { get; set; }
        public double BodyRatesPhysY { get; set; }
        public double BodyRatesPhysZ { get; set; }
        public double PropellantMassKg { get; set; }
        public float MotionlessTime { get; set; }
        public float Draft { get; set; }
        
        // Engine state
        public bool EngineOn { get; set; }
        public float EngineThrottle { get; set; }
        
        // Parent body
        public string ParentBodyId { get; set; } = "Earth";
        
        // Player info
        public string PlayerName { get; set; } = string.Empty;
        public string VehicleId { get; set; } = string.Empty;
        
        // Message type
        public bool IsAcknowledgment { get; set; }
        
        [MemoryPackConstructor]
        public OrbitSyncMessage() : base((GameMessageId)MESSAGE_ID) { }
        
        public override void Execute() { }
    }
}
