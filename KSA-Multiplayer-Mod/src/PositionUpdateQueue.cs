using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Queue for buffering incoming position updates.
    /// Modeled after LMP's PositionUpdateQueue.
    /// 
    /// Each remote vehicle has its own queue. Updates are dequeued
    /// and interpolated by VesselPositionUpdate.
    /// </summary>
    public class PositionUpdateQueue
    {
        private const string LogName = "Queue";
        
        /// <summary>Global dictionary of queues by vehicle key</summary>
        private static readonly ConcurrentDictionary<string, PositionUpdateQueue> _queues = new();
        
        /// <summary>The actual queue of updates</summary>
        private readonly ConcurrentQueue<VesselPositionUpdate> _queue = new();
        
        /// <summary>Object pool for recycling VesselPositionUpdate objects</summary>
        private readonly ConcurrentBag<VesselPositionUpdate> _pool = new();
        
        /// <summary>Maximum queue size to prevent memory issues</summary>
        private const int MaxQueueSize = 50;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        #region Static Methods
        
        /// <summary>
        /// Get or create queue for a vehicle
        /// </summary>
        public static PositionUpdateQueue GetOrCreateQueue(string vehicleKey)
        {
            return _queues.GetOrAdd(vehicleKey, _ => new PositionUpdateQueue());
        }
        
        /// <summary>
        /// Get queue for a vehicle (may be null)
        /// </summary>
        public static PositionUpdateQueue? GetQueue(string vehicleKey)
        {
            return _queues.TryGetValue(vehicleKey, out var queue) ? queue : null;
        }
        
        /// <summary>
        /// Remove queue for a vehicle
        /// </summary>
        public static void RemoveQueue(string vehicleKey)
        {
            if (_queues.TryRemove(vehicleKey, out _))
            {
                Log($"Removed queue for {vehicleKey}");
            }
        }
        
        /// <summary>
        /// Clear all queues
        /// </summary>
        public static void ClearAllQueues()
        {
            _queues.Clear();
            Log("Cleared all queues");
        }
        
        #endregion
        
        #region Instance Methods
        
        /// <summary>
        /// Enqueue a new position update from a network message
        /// </summary>
        public void Enqueue(VehicleStateMessage msg)
        {
            // Get from pool or create new
            VesselPositionUpdate update;
            if (!_pool.TryTake(out update!))
            {
                update = new VesselPositionUpdate();
            }
            
            // Populate from message
            update.VehicleKey = $"{msg.OwnerPlayerName}_{msg.VehicleId}";
            update.ParentBodyId = msg.ParentBodyId ?? "Earth";
            
            // CCI coordinates
            update.PositionCci = new Brutal.Numerics.double3(msg.PositionCciX, msg.PositionCciY, msg.PositionCciZ);
            update.VelocityCci = new Brutal.Numerics.double3(msg.VelocityCciX, msg.VelocityCciY, msg.VelocityCciZ);
            
            // CCF coordinates (for surface situations)
            update.PositionCcf = new Brutal.Numerics.double3(msg.PositionCcfX, msg.PositionCcfY, msg.PositionCcfZ);
            update.VelocityCcf = new Brutal.Numerics.double3(msg.VelocityCcfX, msg.VelocityCcfY, msg.VelocityCcfZ);
            
            update.PhysFrame = msg.PhysFrame;
            update.Orientation = new Brutal.Numerics.doubleQuat(msg.OrientationX, msg.OrientationY, msg.OrientationZ, msg.OrientationW);
            update.BodyRates = new Brutal.Numerics.double3(msg.BodyRatesX, msg.BodyRatesY, msg.BodyRatesZ);
            update.RocketThrusts = msg.RocketThrusts ?? Array.Empty<float>();
            update.GameTimeStamp = msg.StateTimeSeconds;
            update.Situation = msg.Situation;
            update.PingSec = 0;
            
            // Limit queue size - drop oldest if full
            while (_queue.Count >= MaxQueueSize)
            {
                if (_queue.TryDequeue(out var old))
                {
                    Recycle(old);
                }
            }
            
            _queue.Enqueue(update);
        }
        
        /// <summary>
        /// Try to dequeue the next update
        /// </summary>
        public bool TryDequeue(out VesselPositionUpdate? update)
        {
            return _queue.TryDequeue(out update);
        }
        
        /// <summary>
        /// Peek at the next update without removing
        /// </summary>
        public bool TryPeek(out VesselPositionUpdate? update)
        {
            return _queue.TryPeek(out update);
        }
        
        /// <summary>
        /// Return an update to the pool for reuse
        /// </summary>
        public void Recycle(VesselPositionUpdate update)
        {
            // Reset state
            update.Vessel = null;
            update.Target = null;
            update.KsaOrbit = null;
            update.CurrentFrame = 0;
            update.PhysFrame = 0;
            update.PositionCcf = Brutal.Numerics.double3.Zero;
            update.VelocityCcf = Brutal.Numerics.double3.Zero;
            
            _pool.Add(update);
        }
        
        /// <summary>
        /// Number of updates in queue
        /// </summary>
        public int Count => _queue.Count;
        
        /// <summary>
        /// Clear this queue
        /// </summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out var update))
            {
                Recycle(update);
            }
        }
        
        /// <summary>
        /// Drop old updates that are too far in the past
        /// </summary>
        public void DropOldUpdates(double currentTime, double maxAge)
        {
            while (_queue.TryPeek(out var update) && update != null)
            {
                if (currentTime - update.GameTimeStamp > maxAge)
                {
                    if (_queue.TryDequeue(out var old) && old != null)
                    {
                        Recycle(old);
                    }
                }
                else
                {
                    break;
                }
            }
        }
        
        #endregion
    }
}
