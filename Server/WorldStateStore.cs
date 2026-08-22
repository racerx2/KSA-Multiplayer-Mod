namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Holds the server's in-memory vessel designs and states per owner.</summary>
    public sealed class WorldStateStore
    {
        private sealed class VehicleSnapshot
        {
            public byte[]? DesignPacket { get; set; }
            public byte[]? StatePacket { get; set; }
        }

        private readonly Dictionary<string, Dictionary<string, VehicleSnapshot>> _owners =
            new(StringComparer.OrdinalIgnoreCase);

        public void SetDesign(string owner, string vehicleId, byte[] packet) =>
            GetVehicle(owner, vehicleId).DesignPacket = packet;

        public void SetState(string owner, string vehicleId, byte[] packet) =>
            GetVehicle(owner, vehicleId).StatePacket = packet;

        /// <summary>Drops everything an owner had, returning how many vessels went.</summary>
        public int RemoveOwner(string owner)
        {
            if (!_owners.TryGetValue(owner, out var vehicles))
                return 0;

            int count = vehicles.Count;
            _owners.Remove(owner);
            return count;
        }

        public bool Remove(string owner, string vehicleId)
        {
            if (!_owners.TryGetValue(owner, out var vehicles) ||
                !vehicles.Remove(vehicleId))
                return false;
            if (vehicles.Count == 0)
                _owners.Remove(owner);
            return true;
        }

        public IReadOnlyList<byte[]> GetSnapshotPackets(
            string excludeOwner, out int designCount, out int stateCount)
        {
            var packets = new List<byte[]>();
            designCount = 0;
            stateCount = 0;
            foreach (var owner in _owners.OrderBy(pair => pair.Key))
            {
                if (owner.Key.Equals(excludeOwner, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var vehicle in owner.Value.OrderBy(pair => pair.Key))
                {
                    if (vehicle.Value.DesignPacket != null)
                    {
                        packets.Add(vehicle.Value.DesignPacket);
                        designCount++;
                    }
                    if (vehicle.Value.StatePacket != null)
                    {
                        packets.Add(vehicle.Value.StatePacket);
                        stateCount++;
                    }
                }
            }
            return packets;
        }

        private VehicleSnapshot GetVehicle(string owner, string vehicleId)
        {
            if (!_owners.TryGetValue(owner, out var vehicles))
            {
                vehicles = new Dictionary<string, VehicleSnapshot>(StringComparer.Ordinal);
                _owners[owner] = vehicles;
            }
            if (!vehicles.TryGetValue(vehicleId, out VehicleSnapshot? snapshot))
            {
                snapshot = new VehicleSnapshot();
                vehicles[vehicleId] = snapshot;
            }
            return snapshot;
        }
    }
}
