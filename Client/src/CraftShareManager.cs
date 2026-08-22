using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using KSA;
using KSA.Mods.Multiplayer.Messages;
using KSA.Networking;
using Tomlet;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Shares saved craft with the server and installs the ones other players share.</summary>
    public class CraftShareManager
    {
        private const string LogName = "Craft";

        /// <summary>Filename holding a craft's metadata inside its folder.</summary>
        private const string MetaFileName = "meta.toml";

        /// <summary>Filename holding a craft's parts inside its folder.</summary>
        private const string VehicleFileName = "vehicle.xml";

        /// <summary>Prefix marking a folder that is still being written.</summary>
        private const string StagingPrefix = ".mp-incoming-";

        /// <summary>Largest compressed vehicle.xml this client will send.</summary>
        private const int MaxCompressedCraftBytes = 4 * 1024 * 1024;

        /// <summary>Largest vehicle.xml this client will unpack from a download.</summary>
        private const int MaxVehicleXmlBytes = 64 * 1024 * 1024;

        /// <summary>A craft on this machine, either the player's own or one KSA ships.</summary>
        public sealed class SavedCraft
        {
            /// <summary>Craft name, as its meta.toml gives it.</summary>
            public string Name { get; }

            public string SizeText { get; }

            /// <summary>Folder holding the craft's meta.toml and vehicle.xml.</summary>
            public string DirectoryPath { get; }

            /// <summary>Whether this came from KSA's defaultvehicles rather than the player's saves.</summary>
            public bool IsStock { get; }

            /// <summary>Name shown in the picker, marking stock craft.</summary>
            public string DisplayName => IsStock ? $"{Name}  [stock]" : Name;

            public SavedCraft(string name, string sizeText, string directoryPath, bool isStock)
            {
                Name = name;
                SizeText = sizeText;
                DirectoryPath = directoryPath;
                IsStock = isStock;
            }
        }

        private readonly NetworkManager _networkManager;

        /// <summary>Craft the server holds, as of the last catalogue.</summary>
        private CraftLibraryEntry[] _catalogue = Array.Empty<CraftLibraryEntry>();

        /// <summary>Craft saved on this machine, as of the last scan.</summary>
        private List<SavedCraft> _localCraft = new();

        /// <summary>Downloads waiting to be written, so no file work happens on the network path.</summary>
        private readonly ConcurrentQueue<CraftDataMessage> _pendingInstalls = new();

        /// <summary>Craft name this client last uploaded and has not yet seen confirmed.</summary>
        private string _awaitingUploadOf = string.Empty;

        /// <summary>When that upload was sent, so it cannot wait for a reply forever.</summary>
        private DateTime _uploadSentAt = DateTime.MinValue;

        private bool _eventHandlersRegistered;

        /// <summary>Last outcome, for the panel to show.</summary>
        public string StatusText { get; private set; } = string.Empty;

        /// <summary>Whether StatusText reports a failure.</summary>
        public bool StatusIsError { get; private set; }

        /// <summary>Craft ids being fetched, against the moment each was asked for.</summary>
        private readonly ConcurrentDictionary<string, DateTime> _downloadsInFlight =
            new(StringComparer.Ordinal);

        /// <summary>How long a transfer may go unanswered before it is given up on.</summary>
        private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);

        public CraftShareManager(NetworkManager networkManager)
        {
            _networkManager = networkManager;
        }

        /// <summary>Craft the server holds.</summary>
        public IReadOnlyList<CraftLibraryEntry> Catalogue => _catalogue;

        /// <summary>Craft saved on this machine.</summary>
        public IReadOnlyList<SavedCraft> LocalCraft => _localCraft;

        /// <summary>Whether a download of this craft is outstanding.</summary>
        public bool IsDownloading(string craftId) => _downloadsInFlight.ContainsKey(craftId);

        public void Update(double deltaTime)
        {
            if (!_eventHandlersRegistered)
            {
                NetworkPatches.OnCraftLibraryReceived += OnCraftLibraryReceived;
                NetworkPatches.OnCraftDataReceived += OnCraftDataReceived;
                _eventHandlersRegistered = true;
                RefreshLocalCraft();
            }

            // Installs downloads on the game thread, never on the network path.
            while (_pendingInstalls.TryDequeue(out CraftDataMessage? craft))
                InstallCraft(craft);

            ExpireStalledDownloads();
            ExpireStalledUpload();
        }

        /// <summary>Frees rows whose download the server never answered.</summary>
        private void ExpireStalledDownloads()
        {
            if (_downloadsInFlight.IsEmpty) return;

            DateTime cutoff = DateTime.UtcNow - TransferTimeout;
            foreach (KeyValuePair<string, DateTime> pending in _downloadsInFlight)
            {
                if (pending.Value > cutoff) continue;
                if (!_downloadsInFlight.TryRemove(pending.Key, out _)) continue;

                SetStatus("The server did not answer the download.", isError: true);
                ModLogger.LogAlways(LogName, $"DOWNLOAD TIMED OUT: {pending.Key}");
            }
        }

        /// <summary>Stops an upload the server never confirmed from waiting forever.</summary>
        private void ExpireStalledUpload()
        {
            if (_awaitingUploadOf.Length == 0) return;
            if (DateTime.UtcNow - _uploadSentAt < TransferTimeout) return;

            string craftName = _awaitingUploadOf;
            _awaitingUploadOf = string.Empty;
            SetStatus($"The server did not confirm sharing '{craftName}'.", isError: true);
            ModLogger.LogAlways(LogName, $"UPLOAD TIMED OUT: {craftName}");
        }

        /// <summary>Re-reads the craft saved on this machine, reporting whether KSA could.</summary>
        public bool RefreshLocalCraft()
        {
            var found = new List<SavedCraft>();

            try
            {
                ClearAbandonedStaging();

                // Refresh clears KSA's own list first, so a folder it cannot read
                // leaves that list truncated for the rest of the session.
                VehicleSaves.Refresh();

                ReadOnlySpan<VehicleSave> saves = VehicleSaves.AsSpan();
                for (int i = 0; i < saves.Length; i++)
                {
                    VehicleSave save = saves[i];
                    if (save == null || string.IsNullOrEmpty(save.Id)) continue;

                    // The folder can be named differently from the craft, so it is
                    // taken from the save itself rather than rebuilt from the name.
                    string directory = save is UncompressedVehicleSave uncompressed
                        ? uncompressed.Directory.FullName
                        : Path.Combine(VehicleSaves.SaveFolderPath, save.Id);

                    found.Add(new SavedCraft(
                        save.Id, save.SizeStr ?? string.Empty, directory, isStock: false));
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not read the local craft list: {ex}");
                SetStatus(
                    "KSA could not read one of the craft in your vehicle folder, so its " +
                    "vehicle list is incomplete. Remove the bad folder and restart KSA.",
                    isError: true);
                return false;
            }

            // KSA keeps the craft it ships in a separate collection and folder, so a
            // player with nothing saved of their own still has these to share.
            AddStockCraft(found);

            found.Sort((a, b) =>
            {
                if (a.IsStock != b.IsStock) return a.IsStock ? 1 : -1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            _localCraft = found;
            return true;
        }

        /// <summary>
        /// Locates Content/Core/defaultvehicles. KSA's own path is relative to the working
        /// directory, which is the install folder under every launcher that loads the stock
        /// craft at all; the assembly folder is tried as well in case it is not.
        /// </summary>
        private static string FindStockCraftFolder()
        {
            string gamePath = DefaultVehicleSaves.SaveFolderPath;
            if (Directory.Exists(gamePath))
                return gamePath;

            string besideAssembly = Path.Combine(AppContext.BaseDirectory, gamePath);
            if (Directory.Exists(besideAssembly))
                return besideAssembly;

            ModLogger.Log(LogName,
                $"KSA's stock craft folder was not found at '{gamePath}' or '{besideAssembly}'");
            return string.Empty;
        }

        /// <summary>Adds the craft KSA ships in Content/Core/defaultvehicles.</summary>
        private static void AddStockCraft(List<SavedCraft> found)
        {
            try
            {
                string stockFolder = FindStockCraftFolder();
                if (stockFolder.Length == 0) return;

                foreach (string directory in Directory.GetDirectories(stockFolder))
                {
                    string metaPath = Path.Combine(directory, MetaFileName);
                    string vehiclePath = Path.Combine(directory, VehicleFileName);
                    if (!File.Exists(metaPath) || !File.Exists(vehiclePath)) continue;

                    ReadMetaName(metaPath, out string name);
                    if (name.Length == 0)
                        name = Path.GetFileName(directory);
                    if (name.Length == 0) continue;

                    // A craft the player has saved under the same name wins the picker.
                    bool alreadyListed = false;
                    foreach (SavedCraft existing in found)
                    {
                        if (!existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                        alreadyListed = true;
                        break;
                    }

                    if (alreadyListed) continue;

                    long bytes = new FileInfo(vehiclePath).Length;
                    found.Add(new SavedCraft(
                        name, $"{Math.Max(1, bytes / 1024)} KB", directory, isStock: true));
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not read KSA's stock craft: {ex.Message}");
            }
        }

        /// <summary>Asks the server for the current catalogue.</summary>
        public void RequestCatalogue()
        {
            if (!CanTalkToServer(out string reason))
            {
                SetStatus(reason, isError: true);
                return;
            }

            _networkManager.SendToAuthority(new CraftRequestMessage
            {
                RequestKind = CraftRequestMessage.REQUEST_CATALOGUE,
                RequesterPlayerName = LocalPlayerName
            });
        }

        /// <summary>Reads a craft off disk and offers it to the server.</summary>
        public void UploadCraft(SavedCraft? craft)
        {
            if (!CanTalkToServer(out string reason))
            {
                SetStatus(reason, isError: true);
                return;
            }

            if (craft == null || string.IsNullOrWhiteSpace(craft.Name))
            {
                SetStatus("Choose a craft to share.", isError: true);
                return;
            }

            string playerName = LocalPlayerName;
            if (playerName.Length == 0)
            {
                SetStatus("The server has not confirmed your player name yet.", isError: true);
                return;
            }

            string craftName = craft.Name;
            string craftDirectory = craft.DirectoryPath;
            string metaPath = Path.Combine(craftDirectory, MetaFileName);
            string vehiclePath = Path.Combine(craftDirectory, VehicleFileName);

            if (!File.Exists(metaPath) || !File.Exists(vehiclePath))
            {
                SetStatus($"'{craftName}' is no longer on disk.", isError: true);
                RefreshLocalCraft();
                return;
            }

            string metaToml;
            byte[] compressed;
            string systemId;
            string gameVersion;

            try
            {
                metaToml = File.ReadAllText(metaPath);
                compressed = Compress(File.ReadAllBytes(vehiclePath));
                ReadMetaFields(metaToml, out systemId, out gameVersion);
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not read craft '{craftName}': {ex}");
                SetStatus($"Could not read '{craftName}' off disk.", isError: true);
                return;
            }

            if (compressed.Length > MaxCompressedCraftBytes)
            {
                SetStatus(
                    $"'{craftName}' is {compressed.Length / (1024 * 1024)} MB compressed, " +
                    $"over the {MaxCompressedCraftBytes / (1024 * 1024)} MB limit.",
                    isError: true);
                return;
            }

            SendUpload(playerName, craftName, systemId, gameVersion, metaToml, compressed);
        }

        /// <summary>Name of the vessel this player is flying, or empty when there is none.</summary>
        public static string CurrentVesselName()
        {
            try
            {
                Vehicle? vehicle = Program.ControlledVehicle;
                if (vehicle == null || vehicle.IsDisposed) return string.Empty;
                if (string.IsNullOrEmpty(vehicle.Id)) return string.Empty;

                // Another player's craft is theirs to share, not ours.
                if (VesselIdentity.IsRemoteName(vehicle.Id)) return string.Empty;

                return vehicle.Id;
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottled(LogName, "CURRENT_VESSEL",
                    $"Could not read the controlled vessel: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Snapshots the vessel being flown and offers it to the server. KSA's own
        /// savevehicle command builds a craft the same way, through
        /// VehicleSaveData.Create, so no save file has to exist first.
        /// </summary>
        public void UploadCurrentVessel()
        {
            if (!CanTalkToServer(out string reason))
            {
                SetStatus(reason, isError: true);
                return;
            }

            string playerName = LocalPlayerName;
            if (playerName.Length == 0)
            {
                SetStatus("The server has not confirmed your player name yet.", isError: true);
                return;
            }

            string craftName = CurrentVesselName();
            if (craftName.Length == 0)
            {
                SetStatus("You are not flying a vessel of your own.", isError: true);
                return;
            }

            string metaToml;
            byte[] compressed;
            string systemId = Universe.CurrentSystem?.Id ?? string.Empty;
            string gameVersion;

            try
            {
                // Create reads the live vessel out of the current system by id.
                VehicleSaveData design = VehicleSaveData.Create(craftName);
                if (design.RootPartInstance == null)
                {
                    SetStatus($"'{craftName}' has no parts to share.", isError: true);
                    return;
                }

                using var xml = new MemoryStream();
                XmlHelper.SerializeWithoutNaN(VehicleSaves.VehicleSerializer, design, xml);
                compressed = Compress(xml.ToArray());

                var meta = new SaveMetaData
                {
                    Name = craftName,
                    Created = DateTime.UtcNow,
                    Updated = DateTime.UtcNow,
                    Version = VersionInfo.Current,
                    Systems = new[] { systemId }
                };
                gameVersion = meta.Version ?? string.Empty;
                metaToml = TomletMain.TomlStringFrom(meta);
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not snapshot '{craftName}': {ex}");
                SetStatus($"Could not read '{craftName}' off the vessel you are flying.", isError: true);
                return;
            }

            if (compressed.Length > MaxCompressedCraftBytes)
            {
                SetStatus(
                    $"'{craftName}' is {compressed.Length / (1024 * 1024)} MB compressed, " +
                    $"over the {MaxCompressedCraftBytes / (1024 * 1024)} MB limit.",
                    isError: true);
                return;
            }

            SendUpload(playerName, craftName, systemId, gameVersion, metaToml, compressed);
        }

        /// <summary>Sends one craft to the server and starts waiting for its confirmation.</summary>
        private void SendUpload(
            string playerName, string craftName, string systemId,
            string gameVersion, string metaToml, byte[] compressed)
        {
            // The server keeps the trimmed name, so the same name is sent and awaited.
            string sharedName = craftName.Trim();

            _networkManager.SendToAuthority(new CraftUploadMessage
            {
                OwnerPlayerName = playerName,
                CraftName = sharedName,
                SystemId = systemId,
                GameVersion = gameVersion,
                MetaToml = metaToml,
                CompressedVehicleXml = compressed
            });

            _awaitingUploadOf = sharedName;
            _uploadSentAt = DateTime.UtcNow;
            SetStatus($"Sharing '{sharedName}' ({compressed.Length / 1024} KB)...", isError: false);
            ModLogger.Log(LogName, $"UPLOAD: {sharedName}, {compressed.Length} bytes compressed");
        }

        /// <summary>Asks the server for one craft's files.</summary>
        public void DownloadCraft(string craftId)
        {
            if (!CanTalkToServer(out string reason))
            {
                SetStatus(reason, isError: true);
                return;
            }

            if (string.IsNullOrEmpty(craftId))
                return;

            if (!_downloadsInFlight.TryAdd(craftId, DateTime.UtcNow))
                return;

            _networkManager.SendToAuthority(new CraftRequestMessage
            {
                RequestKind = CraftRequestMessage.REQUEST_CRAFT,
                RequesterPlayerName = LocalPlayerName,
                CraftId = craftId
            });

            SetStatus("Downloading...", isError: false);
            ModLogger.Log(LogName, $"DOWNLOAD REQUESTED: {craftId}");
        }

        /// <summary>Releases the network handlers so a later instance is the only listener.</summary>
        public void Shutdown()
        {
            if (_eventHandlersRegistered)
            {
                NetworkPatches.OnCraftLibraryReceived -= OnCraftLibraryReceived;
                NetworkPatches.OnCraftDataReceived -= OnCraftDataReceived;
                _eventHandlersRegistered = false;
            }

            OnDisconnected();
        }

        /// <summary>Clears the state that only makes sense while connected.</summary>
        public void OnDisconnected()
        {
            _catalogue = Array.Empty<CraftLibraryEntry>();
            _downloadsInFlight.Clear();
            _awaitingUploadOf = string.Empty;
            _uploadSentAt = DateTime.MinValue;
            while (_pendingInstalls.TryDequeue(out _)) { }
            StatusText = string.Empty;
            StatusIsError = false;
        }

        private void OnCraftLibraryReceived(CraftLibraryMessage message)
        {
            _catalogue = SanitiseCatalogue(message.Entries);
            ModLogger.Log(LogName, $"CATALOGUE: {_catalogue.Length} craft");

            if (_awaitingUploadOf.Length == 0)
                return;

            // The upload is confirmed once it appears in the catalogue under our name.
            string playerName = LocalPlayerName;
            foreach (CraftLibraryEntry entry in _catalogue)
            {
                if (!entry.CraftName.Equals(_awaitingUploadOf, StringComparison.Ordinal)) continue;
                if (!entry.OwnerPlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)) continue;

                SetStatus($"Shared '{_awaitingUploadOf}'.", isError: false);
                _awaitingUploadOf = string.Empty;
                return;
            }
        }

        /// <summary>Replaces anything missing or out of range in a catalogue off the wire.</summary>
        private static CraftLibraryEntry[] SanitiseCatalogue(CraftLibraryEntry[]? entries)
        {
            if (entries == null || entries.Length == 0)
                return Array.Empty<CraftLibraryEntry>();

            var clean = new List<CraftLibraryEntry>(entries.Length);
            foreach (CraftLibraryEntry entry in entries)
            {
                if (entry == null) continue;

                entry.CraftId ??= string.Empty;
                entry.CraftName ??= string.Empty;
                entry.OwnerPlayerName ??= string.Empty;
                entry.SystemId ??= string.Empty;
                entry.GameVersion ??= string.Empty;

                // A tick count outside the calendar would throw where it is formatted.
                if (entry.SharedUtcTicks < 0 || entry.SharedUtcTicks > DateTime.MaxValue.Ticks)
                    entry.SharedUtcTicks = 0;

                if (entry.SizeBytes < 0)
                    entry.SizeBytes = 0;

                clean.Add(entry);
            }

            return clean.ToArray();
        }

        private void OnCraftDataReceived(CraftDataMessage message)
        {
            // Everything here came off the wire and may be missing.
            message.CraftId ??= string.Empty;
            message.CraftName ??= string.Empty;
            message.OwnerPlayerName ??= string.Empty;
            message.SystemId ??= string.Empty;
            message.GameVersion ??= string.Empty;
            message.MetaToml ??= string.Empty;
            message.CompressedVehicleXml ??= Array.Empty<byte>();
            message.Error ??= string.Empty;

            if (message.CraftId.Length != 0)
                _downloadsInFlight.TryRemove(message.CraftId, out _);

            if (message.Error.Length != 0)
            {
                // A refusal arrives against whichever operation the server was answering.
                if (_awaitingUploadOf.Length != 0 &&
                    message.CraftName.Equals(_awaitingUploadOf, StringComparison.Ordinal))
                    _awaitingUploadOf = string.Empty;

                SetStatus(message.Error, isError: true);
                ModLogger.LogAlways(LogName, $"SERVER REFUSED: {message.Error}");
                return;
            }

            _pendingInstalls.Enqueue(message);
        }

        /// <summary>Writes a downloaded craft into the vehicle folder and re-scans it.</summary>
        private void InstallCraft(CraftDataMessage craft)
        {
            string saveFolder;
            try
            {
                saveFolder = VehicleSaves.SaveFolderPath;
                Directory.CreateDirectory(saveFolder);
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Vehicle folder unavailable: {ex}");
                SetStatus("Your vehicle folder could not be opened.", isError: true);
                return;
            }

            byte[] vehicleXml;
            try
            {
                vehicleXml = Decompress(craft.CompressedVehicleXml);
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not unpack craft '{craft.CraftName}': {ex}");
                SetStatus($"'{craft.CraftName}' did not unpack.", isError: true);
                return;
            }

            if (vehicleXml.Length == 0)
            {
                SetStatus($"'{craft.CraftName}' arrived empty.", isError: true);
                return;
            }

            // A folder KSA cannot parse would break its whole vehicle list on the next
            // scan, so the craft is read here and refused before anything is written.
            if (!VehicleXmlParses(vehicleXml, out string parseProblem))
            {
                SetStatus(
                    $"'{craft.CraftName}' is not a craft this build of KSA can read.",
                    isError: true);
                ModLogger.LogAlways(LogName,
                    $"DOWNLOAD REFUSED '{craft.CraftName}': {parseProblem}");
                return;
            }

            string installName = ChooseInstallName(craft.CraftName, craft.OwnerPlayerName);
            string stagingPath = Path.Combine(saveFolder, StagingPrefix + Guid.NewGuid().ToString("N"));
            string finalPath = Path.Combine(saveFolder, installName);

            try
            {
                // Writes both files away from the vehicle list, then swaps the folder in whole.
                Directory.CreateDirectory(stagingPath);
                File.WriteAllBytes(Path.Combine(stagingPath, VehicleFileName), vehicleXml);
                File.WriteAllText(
                    Path.Combine(stagingPath, MetaFileName),
                    RenameMeta(craft.MetaToml, installName));

                if (Directory.Exists(finalPath))
                    Directory.Delete(finalPath, recursive: true);
                Directory.Move(stagingPath, finalPath);
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not install craft '{craft.CraftName}': {ex}");
                SetStatus($"Could not write '{craft.CraftName}' to disk.", isError: true);
                TryDeleteDirectory(stagingPath);
                return;
            }

            if (!RefreshLocalCraft())
            {
                // The scan broke with the new craft in place. Take it back out and
                // scan again to find out whether it was the cause.
                ModLogger.LogAlways(LogName,
                    $"Vehicle scan failed after installing '{installName}' - removing it");
                TryDeleteDirectory(finalPath);

                if (RefreshLocalCraft())
                {
                    SetStatus(
                        $"'{craft.CraftName}' broke KSA's vehicle list and was removed again.",
                        isError: true);
                }

                return;
            }

            SetStatus(
                $"Installed '{installName}'. Open the Vehicle Editor and load it from VEHICLE SAVES.",
                isError: false);
            ModLogger.LogAlways(LogName,
                $"INSTALLED: {craft.CraftName} by {craft.OwnerPlayerName} as '{installName}'");
        }

        /// <summary>Reads a downloaded vehicle.xml exactly as KSA would, to prove it loads.</summary>
        private static bool VehicleXmlParses(byte[] vehicleXml, out string problem)
        {
            problem = string.Empty;

            try
            {
                using var stream = new MemoryStream(vehicleXml, writable: false);
                using var reader = new StreamReader(stream);

                if (VehicleSaves.VehicleSerializer.Deserialize(reader) is not VehicleSaveData design)
                {
                    problem = "the file holds no vehicle";
                    return false;
                }

                // VehicleSaveData.LoadFrom does this too, and it can throw on its own.
                design.OnDataLoad(Mod.Empty);
                return true;
            }
            catch (Exception ex)
            {
                problem = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>Picks a craft name that does not overwrite one already saved here.</summary>
        private string ChooseInstallName(string craftName, string ownerName)
        {
            string baseName = Sanitize(craftName);
            if (baseName.Length == 0)
                baseName = "Shared Craft";

            if (!NameIsTaken(baseName))
                return baseName;

            string owner = Sanitize(ownerName);
            string withOwner = owner.Length == 0 ? baseName : $"{baseName} ({owner})";
            if (!NameIsTaken(withOwner))
                return withOwner;

            for (int suffix = 2; suffix < 1000; suffix++)
            {
                string candidate = $"{withOwner} {suffix}";
                if (!NameIsTaken(candidate))
                    return candidate;
            }

            return $"{withOwner} {Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }

        /// <summary>Whether a craft of this name is already saved on this machine.</summary>
        private bool NameIsTaken(string name)
        {
            foreach (SavedCraft craft in _localCraft)
            {
                if (craft.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            try
            {
                return Directory.Exists(Path.Combine(VehicleSaves.SaveFolderPath, name));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Rewrites the meta file's name so it matches the folder it is installed under.</summary>
        private static string RenameMeta(string metaToml, string installName)
        {
            try
            {
                SaveMetaData meta = TomletMain.To<SaveMetaData>(metaToml);
                meta.Name = installName;
                return TomletMain.TomlStringFrom(meta);
            }
            catch (Exception ex)
            {
                // Falls back to a fresh meta file rather than installing an unreadable one.
                ModLogger.LogAlways(LogName,
                    $"Could not reuse the shared meta.toml, writing a new one: {ex.Message}");

                string systemId = Universe.CurrentSystem?.Id ?? string.Empty;
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                var builder = new StringBuilder();
                builder.Append("name = \"").Append(EscapeToml(installName)).Append("\"\n");
                builder.Append("created = ").Append(stamp).Append('\n');
                builder.Append("updated = ").Append(stamp).Append('\n');
                builder.Append("version = \"\"\n");
                builder.Append("systems = [ \"").Append(EscapeToml(systemId)).Append("\" ]\n");
                return builder.ToString();
            }
        }

        /// <summary>Reads the system id and game version out of a craft's meta file.</summary>
        private static void ReadMetaFields(string metaToml, out string systemId, out string gameVersion)
        {
            systemId = string.Empty;
            gameVersion = string.Empty;

            try
            {
                SaveMetaData meta = TomletMain.To<SaveMetaData>(metaToml);
                gameVersion = meta.Version ?? string.Empty;
                if (meta.Systems != null && meta.Systems.Length > 0)
                    systemId = meta.Systems[0] ?? string.Empty;
            }
            catch (Exception ex)
            {
                ModLogger.Log(LogName, $"Could not read meta.toml fields: {ex.Message}");
            }
        }

        private static void ReadMetaName(string metaPath, out string metaName)
        {
            metaName = string.Empty;
            try
            {
                SaveMetaData meta = TomletMain.To<SaveMetaData>(File.ReadAllText(metaPath));
                metaName = meta.Name ?? string.Empty;
            }
            catch
            {
                // A craft with an unreadable meta file cannot be matched by name.
            }
        }

        /// <summary>Strips characters that cannot appear in a folder name.</summary>
        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsControl(c)) continue;
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|')
                    continue;
                builder.Append(c);
            }

            return builder.ToString().Trim().TrimEnd('.');
        }

        private static string EscapeToml(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static byte[] Compress(byte[] raw)
        {
            using var compressed = new MemoryStream();
            using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                brotli.Write(raw, 0, raw.Length);
            }

            return compressed.ToArray();
        }

        private static byte[] Decompress(byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0)
                return Array.Empty<byte>();

            using var source = new MemoryStream(compressed);
            using var brotli = new BrotliStream(source, CompressionMode.Decompress);
            using var raw = new MemoryStream();

            // A small payload can unpack without end, so the output is bounded too.
            byte[] buffer = new byte[81920];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (raw.Length + read > MaxVehicleXmlBytes)
                {
                    throw new InvalidDataException(
                        $"The craft unpacks to more than " +
                        $"{MaxVehicleXmlBytes / (1024 * 1024)} MB.");
                }

                raw.Write(buffer, 0, read);
            }

            return raw.ToArray();
        }

        /// <summary>Removes staging folders a previous session left behind.</summary>
        private static void ClearAbandonedStaging()
        {
            try
            {
                string saveFolder = VehicleSaves.SaveFolderPath;
                if (!Directory.Exists(saveFolder)) return;

                foreach (string candidate in Directory.GetDirectories(saveFolder, StagingPrefix + "*"))
                    TryDeleteDirectory(candidate);
            }
            catch (Exception ex)
            {
                ModLogger.Log(LogName, $"Could not clear abandoned staging folders: {ex.Message}");
            }
        }

        /// <summary>Deletes a craft folder, reporting rather than throwing on failure.</summary>
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName, $"Could not delete '{path}': {ex.Message}");
            }
        }

        /// <summary>Player name the server knows this client by.</summary>
        private static string LocalPlayerName =>
            MultiplayerManager.Instance?.LocalPlayerName ?? string.Empty;

        /// <summary>Whether craft messages can reach the server right now.</summary>
        private bool CanTalkToServer(out string reason)
        {
            if (!_networkManager.IsOnline)
            {
                reason = "You are not connected to a server.";
                return false;
            }

            if (_networkManager.IsHost)
            {
                reason = "Craft sharing needs a dedicated server.";
                return false;
            }

            if (Authority.GameAuthorityId.Value == 0)
            {
                reason = "The server has not finished accepting this client yet.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void SetStatus(string text, bool isError)
        {
            StatusText = text;
            StatusIsError = isError;
        }
    }
}
