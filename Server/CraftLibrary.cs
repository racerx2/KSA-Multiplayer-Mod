using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSA.Multiplayer.DedicatedServer
{
    /// <summary>Holds the craft players have shared, on disk beside the server executable.</summary>
    public sealed class CraftLibrary
    {
        /// <summary>Metadata written beside each stored craft.</summary>
        private sealed class ShareRecord
        {
            [JsonPropertyName("craftId")]
            public string CraftId { get; set; } = string.Empty;

            [JsonPropertyName("craftName")]
            public string CraftName { get; set; } = string.Empty;

            [JsonPropertyName("owner")]
            public string OwnerPlayerName { get; set; } = string.Empty;

            [JsonPropertyName("systemId")]
            public string SystemId { get; set; } = string.Empty;

            [JsonPropertyName("gameVersion")]
            public string GameVersion { get; set; } = string.Empty;

            [JsonPropertyName("sharedUtcTicks")]
            public long SharedUtcTicks { get; set; }

            [JsonPropertyName("sizeBytes")]
            public int SizeBytes { get; set; }
        }

        /// <summary>Name of the directory holding one subdirectory per shared craft.</summary>
        public const string LibraryFolderName = "shared_craft";

        private const string RecordFileName = "share.json";
        private const string MetaFileName = "meta.toml";
        private const string VehicleFileName = "vehicle.xml.br";

        /// <summary>Marks a folder that is still being written.</summary>
        private const string StagingSuffix = ".incoming";

        /// <summary>Largest compressed vehicle.xml accepted.</summary>
        public const int MaxCompressedCraftBytes = 4 * 1024 * 1024;

        /// <summary>Largest meta.toml accepted.</summary>
        public const int MaxMetaTomlLength = 64 * 1024;

        /// <summary>Longest craft name accepted.</summary>
        public const int MaxCraftNameLength = 64;

        /// <summary>Most craft the library will hold across every player.</summary>
        public const int MaxCraftInLibrary = 512;

        private readonly Dictionary<string, ShareRecord> _records = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private readonly int _maxPerPlayer;
        private readonly string _libraryPath;

        public CraftLibrary(int maxPerPlayer, string? libraryPath = null)
        {
            _maxPerPlayer = maxPerPlayer < 1 ? 1 : maxPerPlayer;
            _libraryPath = libraryPath
                ?? Path.Combine(AppContext.BaseDirectory, LibraryFolderName);
        }

        /// <summary>Directory holding one subdirectory per shared craft.</summary>
        public string LibraryPath => _libraryPath;

        /// <summary>Number of craft currently stored.</summary>
        public int Count
        {
            get { lock (_lock) { return _records.Count; } }
        }

        /// <summary>Reads every craft already on disk into the index.</summary>
        public void Load()
        {
            lock (_lock)
            {
                _records.Clear();

                if (!Directory.Exists(LibraryPath))
                {
                    Directory.CreateDirectory(LibraryPath);
                    return;
                }

                foreach (string directory in Directory.GetDirectories(LibraryPath))
                {
                    // Clears folders a previous run was interrupted while writing.
                    if (directory.EndsWith(StagingSuffix, StringComparison.Ordinal))
                    {
                        try
                        {
                            Directory.Delete(directory, recursive: true);
                        }
                        catch (Exception ex)
                        {
                            ServerLogger.Log(
                                $"Could not clear abandoned craft folder '{directory}': {ex.Message}");
                        }

                        continue;
                    }

                    string recordPath = Path.Combine(directory, RecordFileName);
                    if (!File.Exists(recordPath))
                    {
                        ServerLogger.Log(
                            $"Shared craft folder '{Path.GetFileName(directory)}' has no {RecordFileName} - ignored");
                        continue;
                    }

                    try
                    {
                        var record = JsonSerializer.Deserialize<ShareRecord>(File.ReadAllText(recordPath));
                        if (record == null || string.IsNullOrEmpty(record.CraftId))
                        {
                            ServerLogger.Log($"Shared craft record '{recordPath}' is empty - ignored");
                            continue;
                        }

                        // The id is also the folder name, and every path is built from
                        // it. A record naming anything else is not trusted.
                        string folderName = Path.GetFileName(directory);
                        if (!record.CraftId.Equals(folderName, StringComparison.Ordinal))
                        {
                            ServerLogger.Log(
                                $"Shared craft record in '{folderName}' claims id " +
                                $"'{record.CraftId}' - ignored");
                            continue;
                        }

                        if (!File.Exists(Path.Combine(directory, MetaFileName)) ||
                            !File.Exists(Path.Combine(directory, VehicleFileName)))
                        {
                            ServerLogger.Log($"Shared craft '{record.CraftId}' is missing its files - ignored");
                            continue;
                        }

                        // The record is a file on disk and may have been edited by hand.
                        record.CraftName ??= string.Empty;
                        record.OwnerPlayerName ??= string.Empty;
                        record.SystemId ??= string.Empty;
                        record.GameVersion ??= string.Empty;
                        if (record.SizeBytes < 0)
                            record.SizeBytes = 0;
                        if (record.SharedUtcTicks < 0 || record.SharedUtcTicks > DateTime.MaxValue.Ticks)
                            record.SharedUtcTicks = 0;

                        _records[record.CraftId] = record;
                    }
                    catch (Exception ex)
                    {
                        ServerLogger.Log($"Shared craft record '{recordPath}' could not be read: {ex.Message}");
                    }
                }

                ServerLogger.Log($"Craft library loaded: {_records.Count} craft");
            }
        }

        /// <summary>Stores an upload, returning why it was refused or null when it was kept.</summary>
        public string? Store(CraftUploadMessage message, out CraftLibraryEntry? entry)
        {
            entry = null;

            string owner = (message.OwnerPlayerName ?? string.Empty).Trim();
            string craftName = (message.CraftName ?? string.Empty).Trim();
            string metaToml = message.MetaToml ?? string.Empty;
            byte[] compressed = message.CompressedVehicleXml ?? Array.Empty<byte>();

            if (owner.Length == 0)
                return "The upload named no owner.";

            if (craftName.Length == 0)
                return "The craft has no name.";

            if (craftName.Length > MaxCraftNameLength)
                return $"Craft names must be {MaxCraftNameLength} characters or fewer.";

            foreach (char c in craftName)
            {
                if (char.IsControl(c))
                    return "The craft name contains a control character.";
            }

            if (metaToml.Length == 0)
                return "The craft has no meta.toml.";

            if (metaToml.Length > MaxMetaTomlLength)
                return "The craft's meta.toml is too large.";

            if (compressed.Length == 0)
                return "The craft has no vehicle data.";

            if (compressed.Length > MaxCompressedCraftBytes)
                return $"The craft is larger than the {MaxCompressedCraftBytes / (1024 * 1024)} MB limit.";

            string craftId = MakeCraftId(owner, craftName);

            lock (_lock)
            {
                // Counts the owner's existing craft, excluding the one being replaced.
                int ownedAlready = 0;
                foreach (ShareRecord existing in _records.Values)
                {
                    if (!existing.OwnerPlayerName.Equals(owner, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (existing.CraftId.Equals(craftId, StringComparison.Ordinal))
                        continue;
                    ownedAlready++;
                }

                if (ownedAlready >= _maxPerPlayer)
                    return $"You have already shared {_maxPerPlayer} craft. Remove one before sharing another.";

                // Caps the library as a whole, so nobody can fill the disk by
                // rejoining under a new name for each upload.
                bool isReplacement = _records.ContainsKey(craftId);
                if (!isReplacement && _records.Count >= MaxCraftInLibrary)
                {
                    return $"This server is holding its limit of {MaxCraftInLibrary} shared craft. " +
                           "An admin must remove one before another can be shared.";
                }

                var record = new ShareRecord
                {
                    CraftId = craftId,
                    CraftName = craftName,
                    OwnerPlayerName = owner,
                    SystemId = message.SystemId ?? string.Empty,
                    GameVersion = message.GameVersion ?? string.Empty,
                    SharedUtcTicks = DateTime.UtcNow.Ticks,
                    SizeBytes = compressed.Length
                };

                try
                {
                    WriteCraft(record, metaToml, compressed);
                }
                catch (Exception ex)
                {
                    ServerLogger.Log($"Could not store craft '{craftId}': {ex}");
                    return "The server could not write the craft to disk.";
                }

                _records[craftId] = record;
                entry = ToEntry(record);
            }

            return null;
        }

        /// <summary>Reads a stored craft, or returns null when it is not there.</summary>
        public CraftDataMessage? Fetch(string craftId)
        {
            ShareRecord record;
            lock (_lock)
            {
                if (!_records.TryGetValue(craftId ?? string.Empty, out ShareRecord? found))
                    return null;
                record = found;
            }

            string directory = Path.Combine(LibraryPath, record.CraftId);

            try
            {
                return new CraftDataMessage
                {
                    CraftId = record.CraftId,
                    CraftName = record.CraftName,
                    OwnerPlayerName = record.OwnerPlayerName,
                    SystemId = record.SystemId,
                    GameVersion = record.GameVersion,
                    MetaToml = File.ReadAllText(Path.Combine(directory, MetaFileName)),
                    CompressedVehicleXml = File.ReadAllBytes(Path.Combine(directory, VehicleFileName))
                };
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Could not read craft '{record.CraftId}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Deletes a stored craft.</summary>
        public bool Remove(string craftId)
        {
            lock (_lock)
            {
                if (!_records.Remove(craftId ?? string.Empty))
                    return false;

                string directory = Path.Combine(LibraryPath, craftId!);
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    ServerLogger.Log($"Could not delete craft folder '{directory}': {ex.Message}");
                }

                return true;
            }
        }

        /// <summary>Finds a stored craft by the name a player typed, or null when there is no match.</summary>
        public string? ResolveCraftId(string craftName)
        {
            string wanted = (craftName ?? string.Empty).Trim();
            if (wanted.Length == 0)
                return null;

            lock (_lock)
            {
                if (_records.ContainsKey(wanted))
                    return wanted;

                foreach (ShareRecord record in _records.Values)
                {
                    if (record.CraftName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        return record.CraftId;
                }
            }

            return null;
        }

        /// <summary>Every stored craft, newest first.</summary>
        public CraftLibraryEntry[] GetCatalogue()
        {
            lock (_lock)
            {
                return _records.Values
                    .OrderByDescending(record => record.SharedUtcTicks)
                    .Select(ToEntry)
                    .ToArray();
            }
        }

        private static CraftLibraryEntry ToEntry(ShareRecord record) => new()
        {
            CraftId = record.CraftId,
            CraftName = record.CraftName,
            OwnerPlayerName = record.OwnerPlayerName,
            SystemId = record.SystemId,
            GameVersion = record.GameVersion,
            SizeBytes = record.SizeBytes,
            SharedUtcTicks = record.SharedUtcTicks
        };

        /// <summary>Writes a craft into a fresh folder, replacing whatever was there.</summary>
        private void WriteCraft(ShareRecord record, string metaToml, byte[] compressed)
        {
            Directory.CreateDirectory(_libraryPath);

            string finalPath = Path.Combine(_libraryPath, record.CraftId);
            string stagingPath = finalPath + StagingSuffix;

            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);

            Directory.CreateDirectory(stagingPath);
            File.WriteAllText(Path.Combine(stagingPath, MetaFileName), metaToml);
            File.WriteAllBytes(Path.Combine(stagingPath, VehicleFileName), compressed);
            File.WriteAllText(
                Path.Combine(stagingPath, RecordFileName),
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));

            // Swaps the finished folder into place so a reader never sees a partial craft.
            if (Directory.Exists(finalPath))
                Directory.Delete(finalPath, recursive: true);
            Directory.Move(stagingPath, finalPath);
        }

        /// <summary>Builds a stable, filesystem-safe id from the owner and craft name.</summary>
        private static string MakeCraftId(string owner, string craftName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(owner + "|" + craftName));
            string suffix = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            return $"{Slug(owner)}__{Slug(craftName)}-{suffix}";
        }

        /// <summary>Reduces a name to characters safe in a folder name.</summary>
        private static string Slug(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
                else if (builder.Length > 0 && builder[^1] != '-')
                    builder.Append('-');
            }

            while (builder.Length > 0 && builder[^1] == '-')
                builder.Length--;

            if (builder.Length == 0)
                builder.Append("craft");

            if (builder.Length > 40)
                builder.Length = 40;

            return builder.ToString();
        }
    }
}
