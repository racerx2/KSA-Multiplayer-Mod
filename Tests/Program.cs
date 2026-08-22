using MemoryPack;
using ClientMessages = KSA.Mods.Multiplayer.Messages;
using ServerMessages = KSA.Multiplayer.DedicatedServer;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var authBytes = MemoryPackSerializer.Serialize(new ClientMessages.AuthStatusMessage
{
    Success = true,
    PlayerName = "Pilot",
    Message = "Ready"
});
var serverAuth = MemoryPackSerializer.Deserialize<ServerMessages.AuthStatusMessage>(authBytes);
Assert(serverAuth?.PlayerName == "Pilot" && serverAuth.Success,
    "AuthStatus protocol schema differs between client and server.");

var rosterBytes = MemoryPackSerializer.Serialize(new ServerMessages.PlayerRosterMessage(
    new[] { "Pilot", "Friend" }));
var clientRoster =
    MemoryPackSerializer.Deserialize<ClientMessages.PlayerRosterMessage>(rosterBytes);
Assert(clientRoster?.PlayerNames.SequenceEqual(new[] { "Pilot", "Friend" }) == true,
    "PlayerRoster protocol schema differs between server and client.");

var stateBytes = MemoryPackSerializer.Serialize(new ClientMessages.VehicleStateMessage
{
    VehicleId = "ship-1",
    OwnerPlayerName = "Pilot",
    ParentBodyId = "Earth",
    StateTimeSeconds = 42,
    PositionCciX = 1,
    PositionCciY = 2,
    PositionCciZ = 3,
    SequenceNumber = 7
});
var serverState = MemoryPackSerializer.Deserialize<ServerMessages.VehicleStateMessage>(stateBytes);
Assert(serverState?.VehicleId == "ship-1" &&
       serverState.OwnerPlayerName == "Pilot" &&
       serverState.SequenceNumber == 7,
    "VehicleState protocol schema differs between client and server.");

var movementQueue = new KSA.Mods.Multiplayer.PositionUpdateQueue();
for (int i = 1; i <= 10; i++)
{
    movementQueue.Enqueue(new ClientMessages.VehicleStateMessage
    {
        VehicleId = "ship-1",
        OwnerPlayerName = "Pilot",
        StateTimeSeconds = i
    });
}
Assert(movementQueue.Count <= 4,
    "Nearby movement queue retained too many stale snapshots.");
Assert(movementQueue.TryDequeueLatest(out var newestMovement) &&
       newestMovement?.GameTimeStamp == 10 &&
       movementQueue.Count == 0,
    "Nearby movement queue did not coalesce to the newest snapshot.");

var designBytes = MemoryPackSerializer.Serialize(new ClientMessages.VehicleDesignSyncMessage
{
    VehicleId = "ship-1",
    OwnerPlayerName = "Pilot",
    TemplateId = "Rocket",
    SequenceNumber = 8,
    CompressedDesignXml = new byte[] { 1, 2, 3, 4 }
});
var serverDesign =
    MemoryPackSerializer.Deserialize<ServerMessages.VehicleDesignSyncMessage>(designBytes);
Assert(serverDesign?.CompressedDesignXml.SequenceEqual(new byte[] { 1, 2, 3, 4 }) == true,
    "Vehicle design payload differs between client and server.");

// Craft sharing: an upload written by the client must be readable by the server.
var uploadBytes = MemoryPackSerializer.Serialize(new ClientMessages.CraftUploadMessage
{
    OwnerPlayerName = "Pilot",
    CraftName = "Lifter Mk2",
    SystemId = "Sol",
    GameVersion = "0.5.2.61",
    MetaToml = "name = \"Lifter Mk2\"\n",
    CompressedVehicleXml = new byte[] { 9, 8, 7, 6, 5 }
});
var serverUpload =
    MemoryPackSerializer.Deserialize<ServerMessages.CraftUploadMessage>(uploadBytes);
Assert(serverUpload?.CraftName == "Lifter Mk2" &&
       serverUpload.OwnerPlayerName == "Pilot" &&
       serverUpload.SystemId == "Sol" &&
       serverUpload.GameVersion == "0.5.2.61" &&
       serverUpload.MetaToml == "name = \"Lifter Mk2\"\n" &&
       serverUpload.CompressedVehicleXml.SequenceEqual(new byte[] { 9, 8, 7, 6, 5 }),
    "CraftUpload protocol schema differs between client and server.");

// Craft sharing: a request written by the client must be readable by the server.
var requestBytes = MemoryPackSerializer.Serialize(new ClientMessages.CraftRequestMessage
{
    RequestKind = ClientMessages.CraftRequestMessage.REQUEST_CRAFT,
    RequesterPlayerName = "Friend",
    CraftId = "pilot__lifter-mk2-ab12cd34"
});
var serverRequest =
    MemoryPackSerializer.Deserialize<ServerMessages.CraftRequestMessage>(requestBytes);
Assert(serverRequest?.RequestKind == ServerMessages.CraftRequestMessage.REQUEST_CRAFT &&
       serverRequest.RequesterPlayerName == "Friend" &&
       serverRequest.CraftId == "pilot__lifter-mk2-ab12cd34",
    "CraftRequest protocol schema differs between client and server.");

// Undock request: a passenger's ask written by the client must read on the server.
var undockAskBytes = MemoryPackSerializer.Serialize(new ClientMessages.UndockRequestMessage
{
    Status = ClientMessages.UndockRequestMessage.STATUS_REQUEST,
    RequesterPlayerName = "Friend",
    OwnerPlayerName = "Pilot",
    StackUid = "Pilot|Rocket",
    ConnectorKind = ClientMessages.VesselStructureMessage.CONNECTOR_DOCKING_PORT,
    ConnectorIndex = 3,
    RequestId = 7
});
var serverAsk =
    MemoryPackSerializer.Deserialize<ServerMessages.UndockRequestMessage>(undockAskBytes);
Assert(serverAsk?.Status == ServerMessages.UndockRequestMessage.STATUS_REQUEST &&
       serverAsk.RequesterPlayerName == "Friend" &&
       serverAsk.OwnerPlayerName == "Pilot" &&
       serverAsk.StackUid == "Pilot|Rocket" &&
       serverAsk.ConnectorKind == ServerMessages.VesselStructureMessage.CONNECTOR_DOCKING_PORT &&
       serverAsk.ConnectorIndex == 3 &&
       serverAsk.RequestId == 7 &&
       serverAsk.Reason == string.Empty,
    "UndockRequest protocol schema differs between client and server.");

// The owner's refusal travels the other way, carrying a reason to display.
var declineBytes = MemoryPackSerializer.Serialize(new ServerMessages.UndockRequestMessage
{
    Status = ServerMessages.UndockRequestMessage.STATUS_DECLINED,
    RequesterPlayerName = "Friend",
    OwnerPlayerName = "Pilot",
    StackUid = "Pilot|Rocket",
    ConnectorKind = ServerMessages.VesselStructureMessage.CONNECTOR_DOCKING_PORT,
    ConnectorIndex = 3,
    RequestId = 7,
    Reason = "Pilot declined"
});
var clientDecline =
    MemoryPackSerializer.Deserialize<ClientMessages.UndockRequestMessage>(declineBytes);
Assert(clientDecline?.Status == ClientMessages.UndockRequestMessage.STATUS_DECLINED &&
       clientDecline.RequestId == 7 &&
       clientDecline.RequesterPlayerName == "Friend" &&
       clientDecline.Reason == "Pilot declined",
    "UndockRequest answer does not survive the crossing back to the client.");

// The three statuses must mean the same thing on both sides, or an accept could
// be read as a decline and the passenger would be told the wrong outcome.
Assert(ClientMessages.UndockRequestMessage.STATUS_REQUEST == ServerMessages.UndockRequestMessage.STATUS_REQUEST &&
       ClientMessages.UndockRequestMessage.STATUS_ACCEPTED == ServerMessages.UndockRequestMessage.STATUS_ACCEPTED &&
       ClientMessages.UndockRequestMessage.STATUS_DECLINED == ServerMessages.UndockRequestMessage.STATUS_DECLINED &&
       ClientMessages.UndockRequestMessage.MESSAGE_ID == ServerMessages.UndockRequestMessage.MESSAGE_ID,
    "UndockRequest status codes or message id differ between client and server.");

// Craft sharing: the catalogue's nested entries must survive the crossing.
var libraryBytes = MemoryPackSerializer.Serialize(new ServerMessages.CraftLibraryMessage(new[]
{
    new ServerMessages.CraftLibraryEntry
    {
        CraftId = "pilot__lifter-mk2-ab12cd34",
        CraftName = "Lifter Mk2",
        OwnerPlayerName = "Pilot",
        SystemId = "Sol",
        GameVersion = "0.5.2.61",
        SizeBytes = 4096,
        SharedUtcTicks = 638000000000000000L
    },
    new ServerMessages.CraftLibraryEntry
    {
        CraftId = "friend__probe-ef56ab78",
        CraftName = "Probe",
        OwnerPlayerName = "Friend",
        SystemId = "Sol",
        GameVersion = "0.5.2.61",
        SizeBytes = 512,
        SharedUtcTicks = 638000000000000001L
    }
}));
var clientLibrary =
    MemoryPackSerializer.Deserialize<ClientMessages.CraftLibraryMessage>(libraryBytes)
    ?? throw new InvalidOperationException(
        "CraftLibrary did not deserialise on the client at all.");
Assert(clientLibrary.Entries.Length == 2,
    "CraftLibrary lost entries crossing from server to client.");
Assert(clientLibrary.Entries[0].CraftId == "pilot__lifter-mk2-ab12cd34" &&
       clientLibrary.Entries[0].CraftName == "Lifter Mk2" &&
       clientLibrary.Entries[0].OwnerPlayerName == "Pilot" &&
       clientLibrary.Entries[0].SizeBytes == 4096 &&
       clientLibrary.Entries[0].SharedUtcTicks == 638000000000000000L,
    "CraftLibraryEntry protocol schema differs between server and client.");
Assert(clientLibrary.Entries[1].CraftName == "Probe",
    "CraftLibrary entry order changed crossing from server to client.");

// Craft sharing: delivered craft data must survive the crossing.
var craftDataBytes = MemoryPackSerializer.Serialize(new ServerMessages.CraftDataMessage
{
    CraftId = "pilot__lifter-mk2-ab12cd34",
    CraftName = "Lifter Mk2",
    OwnerPlayerName = "Pilot",
    SystemId = "Sol",
    GameVersion = "0.5.2.61",
    MetaToml = "name = \"Lifter Mk2\"\n",
    CompressedVehicleXml = new byte[] { 1, 1, 2, 3, 5, 8 },
    Error = string.Empty
});
var clientCraftData =
    MemoryPackSerializer.Deserialize<ClientMessages.CraftDataMessage>(craftDataBytes);
Assert(clientCraftData?.CraftName == "Lifter Mk2" &&
       clientCraftData.MetaToml == "name = \"Lifter Mk2\"\n" &&
       clientCraftData.Error.Length == 0 &&
       clientCraftData.CompressedVehicleXml.SequenceEqual(new byte[] { 1, 1, 2, 3, 5, 8 }),
    "CraftData protocol schema differs between server and client.");

// A name the server would refuse must be caught before a connection is opened: KSA's
// own refusal handler disposes the network session from inside the packet loop that is
// still reading the refusal, which crashes the process.
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("Player") != null,
    "The default placeholder name was not caught client-side.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("player") != null,
    "The placeholder name check is case sensitive.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("  Player  ") != null,
    "The placeholder name check did not trim.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("") != null,
    "An empty name was allowed to connect.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("   ") != null,
    "A whitespace-only name was allowed to connect.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName(null) != null,
    "A null name was allowed to connect.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("Racer|X") != null,
    "A name containing the uid separator was allowed to connect.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName(new string('a', 33)) != null,
    "An over-long name was allowed to connect.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName(new string('a', 32)) == null,
    "A name at the length limit was refused.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("racerx2") == null,
    "An ordinary name was refused.");
Assert(KSA.Mods.Multiplayer.MultiplayerManager.ValidatePlayerName("Player2") == null,
    "A name that merely starts with the placeholder was refused.");

// A malformed payload must not quietly produce null. KSA's OnPeerPacket calls
// Shutdown() on a null deserialisation result, from inside the packet loop that still
// holds the packet, so a null is as fatal as an exception.
static string ProbeMalformed(byte[] payload)
{
    try
    {
        var m = KSA.Networking.Messages.GameMessage
            .Deserialise<ClientMessages.CraftUploadMessage>(payload);
        return m == null ? "null" : "object";
    }
    catch (Exception ex)
    {
        return ex.GetType().Name;
    }
}

string truncated = ProbeMalformed(new byte[] { 0x05 });
string garbage = ProbeMalformed(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
string empty = ProbeMalformed(Array.Empty<byte>());
Console.WriteLine($"  malformed payload behaviour: truncated={truncated} garbage={garbage} empty={empty}");
Assert(truncated != "object" && garbage != "object",
    "A malformed payload deserialised into an object - the guard's premise is wrong.");

var world = new ServerMessages.WorldStateStore();
world.SetDesign("Pilot", "ship-a", new byte[] { 202, 1 });
world.SetState("Pilot", "ship-a", new byte[] { 200, 2 });
world.SetDesign("Friend", "ship-b", new byte[] { 202, 3 });
var lateJoinSnapshot = world.GetSnapshotPackets(
    "LateJoiner", out int designs, out int states);
Assert(designs == 2 && states == 1 && lateJoinSnapshot.Count == 3,
    "Late joiners did not receive every cached design and state.");
var ownerSnapshot = world.GetSnapshotPackets(
    "Pilot", out designs, out states);
Assert(designs == 1 && states == 0 && ownerSnapshot.Count == 1,
    "A reconnecting owner received duplicate copies of their own vessels.");
// A player's vessels must leave with them, or every later joiner rebuilds the craft
// of players who left sessions ago and the store grows for the life of the process.
var departing = new ServerMessages.WorldStateStore();
departing.SetDesign("Leaver", "ship-x", new byte[] { 202, 9 });
departing.SetState("Leaver", "ship-x", new byte[] { 200, 9 });
departing.SetDesign("Leaver", "ship-y", new byte[] { 202, 8 });
departing.SetDesign("Stayer", "ship-z", new byte[] { 202, 7 });
Assert(departing.RemoveOwner("Leaver") == 2,
    "Removing a departing player did not drop both of their vessels.");
departing.GetSnapshotPackets("NewJoiner", out int leftDesigns, out int leftStates);
Assert(leftDesigns == 1 && leftStates == 0,
    "A departed player's vessels were still replayed to a new joiner.");
Assert(departing.RemoveOwner("NeverHere") == 0,
    "Removing an unknown owner reported vessels that never existed.");

Assert(world.Remove("Pilot", "ship-a"), "Vessel removal was not accepted.");
world.GetSnapshotPackets("LateJoiner", out designs, out states);
Assert(designs == 1 && states == 0,
    "Removed vessel remained in the authoritative world snapshot.");

static ServerMessages.CraftUploadMessage Upload(string owner, string name, byte[] payload) =>
    new()
    {
        OwnerPlayerName = owner,
        CraftName = name,
        SystemId = "Sol",
        GameVersion = "0.5.2.61",
        MetaToml = $"name = \"{name}\"\n",
        CompressedVehicleXml = payload
    };

string libraryPath = Path.Combine(
    Path.GetTempPath(), $"ksa-craft-{Guid.NewGuid():N}");
try
{
    var library = new ServerMessages.CraftLibrary(2, libraryPath);
    library.Load();
    Assert(library.Count == 0, "A fresh craft library was not empty.");

    Assert(library.Store(Upload("Pilot", "Lifter", new byte[] { 1, 2, 3 }), out var stored) == null &&
           stored?.CraftName == "Lifter",
        "Storing a craft failed.");

    var fetched = library.Fetch(stored!.CraftId);
    Assert(fetched?.CraftName == "Lifter" &&
           fetched.OwnerPlayerName == "Pilot" &&
           fetched.MetaToml == "name = \"Lifter\"\n" &&
           fetched.CompressedVehicleXml.SequenceEqual(new byte[] { 1, 2, 3 }),
        "A stored craft did not come back off disk unchanged.");

    // Re-sharing the same craft replaces it rather than filling the player's quota.
    Assert(library.Store(Upload("Pilot", "Lifter", new byte[] { 4, 5, 6 }), out _) == null &&
           library.Count == 1,
        "Re-sharing a craft did not replace the previous copy.");
    Assert(library.Fetch(stored.CraftId)!.CompressedVehicleXml
               .SequenceEqual(new byte[] { 4, 5, 6 }),
        "Re-sharing a craft did not overwrite its payload.");

    Assert(library.Store(Upload("Pilot", "Probe", new byte[] { 7 }), out _) == null,
        "The second craft was refused below the per-player limit.");
    Assert(library.Store(Upload("Pilot", "Rover", new byte[] { 8 }), out _) != null,
        "The per-player craft limit was not enforced.");
    Assert(library.Store(Upload("Friend", "Rover", new byte[] { 9 }), out _) == null,
        "One player's quota blocked another player.");

    Assert(library.Store(Upload("Pilot", "", new byte[] { 1 }), out _) != null,
        "An unnamed craft was accepted.");
    Assert(library.Store(Upload("Pilot", "Lifter", Array.Empty<byte>()), out _) != null,
        "A craft with no vehicle data was accepted.");

    Assert(library.ResolveCraftId("Probe") != null,
        "A craft could not be found by name.");
    Assert(library.ResolveCraftId("Nothing") == null,
        "A craft name that was never shared resolved to something.");

    // A malformed upload must be refused, never crash the handler.
    Assert(library.Store(new ServerMessages.CraftUploadMessage(), out _) != null,
        "An entirely empty upload was accepted.");

    // The index must survive a restart.
    var reloaded = new ServerMessages.CraftLibrary(2, libraryPath);
    reloaded.Load();
    Assert(reloaded.Count == 3, "The craft library did not persist across a restart.");
    Assert(reloaded.Fetch(stored.CraftId)!.CompressedVehicleXml
               .SequenceEqual(new byte[] { 4, 5, 6 }),
        "A craft's payload changed across a restart.");

    Assert(reloaded.Remove(stored.CraftId) && reloaded.Count == 2,
        "Removing a craft failed.");
    Assert(reloaded.Fetch(stored.CraftId) == null,
        "A removed craft was still served.");
    Assert(!Directory.Exists(Path.Combine(libraryPath, stored.CraftId)),
        "Removing a craft left its folder on disk.");

    // A craft name full of path separators must not produce an id that escapes
    // the library folder.
    Assert(reloaded.Store(Upload("Pilot", "../../escape", new byte[] { 1 }), out var escaped) == null &&
           escaped != null &&
           !escaped.CraftId.Contains("..") &&
           escaped.CraftId.IndexOfAny(new[] { '/', '\\' }) < 0,
        "A craft name with path separators produced an id that leaves the library folder.");

    // A record naming an id other than its own folder must be ignored.
    string plantedFolder = Path.Combine(libraryPath, "planted");
    Directory.CreateDirectory(plantedFolder);
    File.WriteAllText(Path.Combine(plantedFolder, "meta.toml"), "name = \"Planted\"\n");
    File.WriteAllBytes(Path.Combine(plantedFolder, "vehicle.xml.br"), new byte[] { 1 });
    File.WriteAllText(Path.Combine(plantedFolder, "share.json"),
        "{\"craftId\":\"../escape\",\"craftName\":\"Planted\",\"owner\":\"Pilot\"," +
        "\"systemId\":\"Sol\",\"gameVersion\":\"\",\"sharedUtcTicks\":0,\"sizeBytes\":1}");

    // A hand-edited record must not reach the UI with values that cannot be shown.
    string clockFolder = Path.Combine(libraryPath, "badclock");
    Directory.CreateDirectory(clockFolder);
    File.WriteAllText(Path.Combine(clockFolder, "meta.toml"), "name = \"Badclock\"\n");
    File.WriteAllBytes(Path.Combine(clockFolder, "vehicle.xml.br"), new byte[] { 1 });
    File.WriteAllText(Path.Combine(clockFolder, "share.json"),
        "{\"craftId\":\"badclock\",\"craftName\":\"Badclock\",\"owner\":\"Pilot\"," +
        "\"systemId\":\"Sol\",\"gameVersion\":\"\",\"sharedUtcTicks\":-99,\"sizeBytes\":-4}");

    var hardened = new ServerMessages.CraftLibrary(4, libraryPath);
    hardened.Load();
    Assert(hardened.ResolveCraftId("Planted") == null,
        "A share record claiming another folder's id was trusted.");
    Assert(hardened.ResolveCraftId("Badclock") != null,
        "A record with out-of-range numbers was dropped instead of corrected.");
    foreach (var entry in hardened.GetCatalogue())
    {
        Assert(entry.SharedUtcTicks >= 0 && entry.SharedUtcTicks <= DateTime.MaxValue.Ticks,
            "A catalogue entry carried a timestamp that cannot be formatted.");
        Assert(entry.SizeBytes >= 0,
            "A catalogue entry carried a negative size.");
    }
}
finally
{
    if (Directory.Exists(libraryPath)) Directory.Delete(libraryPath, recursive: true);
}

Console.WriteLine("Protocol and craft library tests passed.");
