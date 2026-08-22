using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Buffers incoming position updates for one remote vehicle.</summary>
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
        // Holds a small interpolation buffer.
        private const int MaxQueueSize = 4;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>Per-vessel sample timing used to refuse stale samples.</summary>
        private sealed class Freshness
        {
            /// <summary>Smoothed age of accepted samples: our clock minus the sender's.</summary>
            public double SmoothedOffset;
            public bool Seeded;
            public int ConsecutiveRefusals;
        }

        private static readonly ConcurrentDictionary<string, Freshness> _freshness = new();

        /// <summary>Seconds a sample may exceed the running offset before it is refused.</summary>
        private const double OutlierSeconds = 3.0;

        /// <summary>Weight given to each newly accepted sample's offset.</summary>
        private const double OffsetSmoothing = 0.1;

        /// <summary>Consecutive refusals before a sample is accepted and the offset re-seeded.</summary>
        private const int RefusalsBeforeReseed = 30;

        /// <summary>Decides whether a sample is fresh enough to render from.</summary>
        private static bool AcceptSample(string key, double senderTime)
        {
            double offset = Universe.GetElapsedTime().Seconds() - senderTime;
            Freshness f = _freshness.GetOrAdd(key, _ => new Freshness());

            lock (f)
            {
                if (!f.Seeded)
                {
                    f.SmoothedOffset = offset;
                    f.Seeded = true;
                    f.ConsecutiveRefusals = 0;
                    return true;
                }

                double excess = offset - f.SmoothedOffset;

                if (excess > OutlierSeconds && f.ConsecutiveRefusals < RefusalsBeforeReseed)
                {
                    f.ConsecutiveRefusals++;
                    ModLogger.LogThrottled(LogName, $"STALE_{key}",
                        $"{key}: refusing T={senderTime:F3}s - {offset:F1}s old against our clock where " +
                        $"{f.SmoothedOffset:F1}s is normal for this craft. Rendering it would propagate " +
                        $"{excess:F1}s of orbit that has not happened. ({f.ConsecutiveRefusals} in a row)");
                    return false;
                }

                if (f.ConsecutiveRefusals >= RefusalsBeforeReseed)
                {
                    // Accept this sample and re-seed the expected offset.
                    Log($"{key}: {f.ConsecutiveRefusals} refusals in a row - the sender's clock has moved, " +
                        $"re-seeding the expected offset from {f.SmoothedOffset:F1}s to {offset:F1}s");
                    f.SmoothedOffset = offset;
                    f.ConsecutiveRefusals = 0;
                    return true;
                }

                f.SmoothedOffset += (offset - f.SmoothedOffset) * OffsetSmoothing;
                f.ConsecutiveRefusals = 0;
                return true;
            }
        }

        #region Static Methods
        
        /// <summary>Gets or creates the queue for a vehicle.</summary>
        public static PositionUpdateQueue GetOrCreateQueue(string vehicleKey)
        {
            return _queues.GetOrAdd(vehicleKey, _ => new PositionUpdateQueue());
        }
        
        /// <summary>Gets the queue for a vehicle, or null.</summary>
        public static PositionUpdateQueue? GetQueue(string vehicleKey)
        {
            return _queues.TryGetValue(vehicleKey, out var queue) ? queue : null;
        }
        
        /// <summary>Removes the queue for a vehicle.</summary>
        /// <remarks>
        /// The vessel's freshness record goes with it. Keeping it would hold a
        /// clock offset measured against a vessel that is gone, and the offset
        /// would still be there if the same uid came back — after a reconnect,
        /// or after the owner jumped their clock — where it would refuse
        /// <see cref="RefusalsBeforeReseed"/> samples in a row before
        /// re-seeding, leaving the vessel frozen for about two seconds.
        /// </remarks>
        public static void RemoveQueue(string vehicleKey)
        {
            bool hadQueue = _queues.TryRemove(vehicleKey, out _);
            _freshness.TryRemove(vehicleKey, out _);
            if (hadQueue)
            {
                Log($"Removed queue for {vehicleKey}");
            }
        }
        
        /// <summary>Clears all queues.</summary>
        public static void ClearAllQueues()
        {
            _queues.Clear();
            _freshness.Clear();
            Log("Cleared all queues");
        }
        
        /// <summary>
        /// Forgets every measured clock offset, keeping the queues.
        /// </summary>
        /// <remarks>
        /// Called when this client's own clock jumps. Every offset was measured
        /// against the old clock, so all of them are wrong by the size of the
        /// jump at once; re-seeding from the next sample of each vessel is both
        /// faster and more accurate than letting the refusal counter work its
        /// way through thirty samples per vessel.
        /// </remarks>
        public static void ResetFreshness()
        {
            _freshness.Clear();
            Log("Cleared sample freshness after a clock jump");
        }
        
        #endregion
        
        #region Instance Methods
        
        /// <summary>Enqueues a new position update from a network message.</summary>
        public void Enqueue(VehicleStateMessage msg)
        {
            string key = VesselIdentity.UidFromWire(msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty);

            // Refuse a sample far older than the ones around it.
            if (!AcceptSample(key, msg.StateTimeSeconds)) return;

            // Get from pool or create new
            VesselPositionUpdate update;
            if (!_pool.TryTake(out update!))
            {
                update = new VesselPositionUpdate();
            }
            
            // Populate from message
            update.VehicleKey = VesselIdentity.UidFromWire(msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty);
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
            // Map the wire situation to the remote situation.
            update.Situation = VesselPositionUpdate.RemoteSituation(msg.Situation);
            update.PingSec = 0;
            update.ArrivalTimeSeconds = VesselPositionUpdate.GetMonotonicSeconds();
            
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
        
        /// <summary>Dequeues the next update.</summary>
        public bool TryDequeue(out VesselPositionUpdate? update)
        {
            return _queue.TryDequeue(out update);
        }

        /// <summary>Drains queued snapshots and returns only the newest.</summary>
        public bool TryDequeueLatest(out VesselPositionUpdate? update)
        {
            update = null;
            while (_queue.TryDequeue(out VesselPositionUpdate? candidate))
            {
                if (update != null)
                    Recycle(update);
                update = candidate;
            }
            return update != null;
        }
        
        /// <summary>Peeks at the next update without removing it.</summary>
        public bool TryPeek(out VesselPositionUpdate? update)
        {
            return _queue.TryPeek(out update);
        }
        
        /// <summary>Returns an update to the pool for reuse.</summary>
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
        
        /// <summary>Number of updates in the queue.</summary>
        public int Count => _queue.Count;
        
        /// <summary>Clears this queue.</summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out var update))
            {
                Recycle(update);
            }
        }
        
        // Age-based dropping used to live here. The queue is hard-bounded at
        // MaxQueueSize and Enqueue evicts the oldest entry to make room, so
        // nothing can accumulate for long enough to need it.
        
        #endregion
    }
}
