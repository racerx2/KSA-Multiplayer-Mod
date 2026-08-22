using System;
using HarmonyLib;
using KSA;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Lets a docked passenger ask the owner of the merged stack to undock them.
    /// </summary>
    /// <remarks>
    /// The player who initiates a dock inherits control of the merged vessel, so
    /// on every other client that stack is a remote vessel. A remote vessel is
    /// deliberately kept out of the physics bubbles, and KSA's Vehicle.Split
    /// refuses a vessel with no bubble — which is why undocking from the
    /// passenger's side used to fail with "does not belong to an update task".
    /// Granting a bubble would not fix it: VesselStructure.SplitPrefix will not
    /// announce a split of a vessel this client does not own, so the split would
    /// happen here and nowhere else and the part trees would diverge. The split
    /// has to run on the owner's machine, where it replicates back through the
    /// ordinary VesselStructureMessage path.
    /// </remarks>
    public static class UndockRequests
    {
        private const string LogName = "Structure";

        /// <summary>How long the owner has to answer before the ask is dropped.</summary>
        private const double PromptSeconds = 30.0;

        /// <summary>
        /// How long the requester waits for an answer.
        /// </summary>
        /// <remarks>
        /// Longer than <see cref="PromptSeconds"/> so that in the ordinary case
        /// the owner's own timeout fires first and the requester is told the ask
        /// lapsed, rather than both sides giving up independently. This deadline
        /// is what covers the owner's client crashing or quitting mid-prompt,
        /// where no answer is ever sent.
        /// </remarks>
        private const double AwaitSeconds = 40.0;

        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>Name this client plays under. Set by MultiplayerManager.</summary>
        public static string LocalPlayerName { get; set; } = string.Empty;

        /// <summary>Raised with a message this client should put on the wire.</summary>
        public static event Action<UndockRequestMessage>? OnSend;

        /// <summary>An ask this client has sent and is waiting on.</summary>
        private sealed class Outgoing
        {
            public uint RequestId;
            public string Owner = string.Empty;
            public string StackUid = string.Empty;
            public DateTime Deadline;
        }

        /// <summary>An ask from another player, waiting for this player's answer.</summary>
        public sealed class Prompt
        {
            public uint RequestId;
            public string Requester = string.Empty;
            public string StackUid = string.Empty;
            public string StackName = string.Empty;
            public byte ConnectorKind;
            public int ConnectorIndex;
            public DateTime Deadline;

            public double SecondsLeft => Math.Max(0.0, (Deadline - DateTime.UtcNow).TotalSeconds);
        }

        private static uint _nextRequestId;
        private static Outgoing? _outgoing;

        /// <summary>The ask awaiting this player's answer, or null. Read by the GUI.</summary>
        public static Prompt? Pending { get; private set; }

        /// <summary>Whether this client is waiting on an answer of its own.</summary>
        public static bool IsWaiting => _outgoing != null;

        /// <summary>Who this client is waiting on, for the GUI to name.</summary>
        public static string WaitingOn => _outgoing?.Owner ?? string.Empty;

        public static void Reset()
        {
            _outgoing = null;
            Pending = null;
        }

        // ------------------------------------------------------------ patching

        /// <summary>Installs the undock interception.</summary>
        /// <remarks>
        /// Contained in its own try: this is called from the middle of
        /// VesselStructure.ApplyPatches, and a throw here would abandon the
        /// patches that follow it. Losing this one costs the passenger a clear
        /// message; losing the docking readout or the input-drain hook costs far
        /// more, so the failure is logged and the rest is left to install.
        /// </remarks>
        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                var undock = AccessTools.Method(typeof(DockingPort), "Undock");
                if (undock == null)
                {
                    Log("WARNING: DockingPort.Undock not found - a passenger's undock will " +
                        "reach KSA and fail with an update-task error");
                    return;
                }

                harmony.Patch(undock,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(UndockRequests), nameof(UndockPrefix))));
                Log("Patched DockingPort.Undock - a passenger's undock is forwarded to the stack's owner");
            }
            catch (Exception ex)
            {
                Log($"WARNING: could not patch DockingPort.Undock ({ex.Message}) - a passenger's " +
                    "undock will reach KSA and fail with an update-task error");
            }
        }

        /// <summary>
        /// Turns an undock of someone else's stack into a request to its owner.
        /// </summary>
        /// <remarks>
        /// Returns true for every vessel this client owns, so an ordinary undock
        /// is untouched. For a stack owned by another player the original is
        /// skipped entirely: reaching it would only produce KSA's error, because
        /// the vessel has no physics bubble.
        /// </remarks>
        public static bool UndockPrefix(DockingPort __instance, Vehicle oldVehicle,
                                        ref PoseChange combinedToSplit, ref Vehicle? __result)
        {
            try
            {
                if (oldVehicle == null || __instance == null) return true;
                if (!VesselIdentity.IsRemoteName(oldVehicle.Id)) return true;

                combinedToSplit = PoseChange.Identity;
                __result = null;
                Ask(oldVehicle, __instance);
                return false;
            }
            catch (Exception ex)
            {
                // A failure here must not let the undock through to KSA, which
                // would answer with the error this patch exists to replace.
                Log($"UNDOCK ASK FAILED: {ex.Message}");
                Notify("Could not ask for an undock - see the log.");
                combinedToSplit = PoseChange.Identity;
                __result = null;
                return false;
            }
        }

        // ------------------------------------------------------- passenger side

        private static void Ask(Vehicle stack, DockingPort port)
        {
            string uid = VesselIdentity.UidFromLocalName(stack.Id, LocalPlayerName);
            if (!VesselIdentity.TryParseUid(uid, out string owner, out _) || string.IsNullOrEmpty(owner))
            {
                Notify($"Cannot tell who owns {stack.Id} - undock not sent.");
                Log($"UNDOCK ASK REFUSED: no owner in uid '{uid}'");
                return;
            }

            var manager = MultiplayerManager.Instance;
            if (manager == null || !manager.IsConnected)
            {
                Notify("Not connected - only the stack's owner can undock it.");
                return;
            }

            bool ownerIsOnline = false;
            foreach (string player in manager.ConnectedPlayers)
            {
                if (string.Equals(player, owner, StringComparison.Ordinal)) { ownerIsOnline = true; break; }
            }

            if (!ownerIsOnline)
            {
                Notify($"{owner} is not connected - they have to undock this stack.");
                Log($"UNDOCK ASK REFUSED: {owner} is not in the player list");
                return;
            }

            if (_outgoing != null)
            {
                Notify($"Already waiting on {_outgoing.Owner}.");
                return;
            }

            if (!VesselStructure.TryAddressPort(stack, port, out int index))
            {
                Notify("That port is not on the stack - undock not sent.");
                Log($"UNDOCK ASK REFUSED: port not found in {stack.Id}'s docking port list");
                return;
            }

            uint id = ++_nextRequestId;
            _outgoing = new Outgoing
            {
                RequestId = id,
                Owner = owner,
                StackUid = uid,
                Deadline = DateTime.UtcNow.AddSeconds(AwaitSeconds)
            };

            Log($"UNDOCK ASK #{id}: asking {owner} to undock {uid} at port #{index}");
            Notify($"Asked {owner} to undock you.");

            OnSend?.Invoke(new UndockRequestMessage
            {
                Status = UndockRequestMessage.STATUS_REQUEST,
                RequesterPlayerName = LocalPlayerName,
                OwnerPlayerName = owner,
                StackUid = uid,
                ConnectorKind = VesselStructureMessage.CONNECTOR_DOCKING_PORT,
                ConnectorIndex = index,
                RequestId = id
            });
        }

        // ----------------------------------------------------------- owner side

        /// <summary>Handles a request or an answer arriving from another player.</summary>
        public static void HandleIncoming(UndockRequestMessage msg)
        {
            if (msg == null) return;

            try
            {
                if (msg.Status == UndockRequestMessage.STATUS_REQUEST)
                    HandleRequest(msg);
                else
                    HandleAnswer(msg);
            }
            catch (Exception ex)
            {
                Log($"UNDOCK MESSAGE FAILED: {ex.Message}");
            }
        }

        private static void HandleRequest(UndockRequestMessage msg)
        {
            // The server relays to everyone; only the named owner acts.
            if (!string.Equals(msg.OwnerPlayerName, LocalPlayerName, StringComparison.Ordinal))
                return;

            Log($"UNDOCK ASKED #{msg.RequestId}: {msg.RequesterPlayerName} wants off {msg.StackUid} " +
                $"at port #{msg.ConnectorIndex}");

            if (msg.ConnectorKind != VesselStructureMessage.CONNECTOR_DOCKING_PORT)
            {
                Answer(msg, UndockRequestMessage.STATUS_DECLINED, "that is not a docking port");
                return;
            }

            if (Pending != null)
            {
                Answer(msg, UndockRequestMessage.STATUS_DECLINED,
                    $"{LocalPlayerName} is already answering another undock");
                return;
            }

            Vehicle? stack = VesselStructure.ResolveVessel(msg.StackUid);
            if (stack == null || stack.IsDisposed)
            {
                Answer(msg, UndockRequestMessage.STATUS_DECLINED, "that stack is not here");
                return;
            }

            if (VesselStructure.DockingPortAt(stack, msg.ConnectorIndex) == null)
            {
                Answer(msg, UndockRequestMessage.STATUS_DECLINED, "that port is not on the stack");
                return;
            }

            // Vehicle.Split refuses a vessel with no physics bubble - the same
            // refusal the passenger just hit. Better to say so now than to
            // accept, tell them they are being let off, and have the split
            // quietly return null. The stack has to be in physics here, which
            // in practice means this player is flying or standing next to it.
            if (stack.PhysicsBubble == null)
            {
                Answer(msg, UndockRequestMessage.STATUS_DECLINED,
                    $"{LocalPlayerName} is not close enough to the stack to undock it");
                return;
            }

            Pending = new Prompt
            {
                RequestId = msg.RequestId,
                Requester = msg.RequesterPlayerName,
                StackUid = msg.StackUid,
                StackName = stack.Id,
                ConnectorKind = msg.ConnectorKind,
                ConnectorIndex = msg.ConnectorIndex,
                Deadline = DateTime.UtcNow.AddSeconds(PromptSeconds)
            };
        }

        /// <summary>Performs the undock the pending request asked for.</summary>
        public static void Allow()
        {
            Prompt? p = Pending;
            Pending = null;
            if (p == null) return;

            Vehicle? stack = VesselStructure.ResolveVessel(p.StackUid);
            DockingPort? port = stack == null ? null : VesselStructure.DockingPortAt(stack, p.ConnectorIndex);

            if (stack == null || stack.IsDisposed || port == null)
            {
                Log($"UNDOCK ALLOW #{p.RequestId}: {p.StackUid} or port #{p.ConnectorIndex} " +
                    "is no longer here");
                AnswerTo(p, UndockRequestMessage.STATUS_DECLINED, "the stack changed before it could be undocked");
                return;
            }

            if (!port.Docked)
            {
                Log($"UNDOCK ALLOW #{p.RequestId}: port #{p.ConnectorIndex} is already detached");
                AnswerTo(p, UndockRequestMessage.STATUS_DECLINED, "that port is already detached");
                return;
            }

            // Re-checked here, not only when the ask arrived: the prompt sits on
            // screen for up to half a minute, and this player may have warped or
            // switched vessels in the meantime.
            if (stack.PhysicsBubble == null)
            {
                Log($"UNDOCK ALLOW #{p.RequestId}: {stack.Id} left physics while the prompt was up");
                AnswerTo(p, UndockRequestMessage.STATUS_DECLINED,
                    $"{LocalPlayerName} moved away from the stack before undocking it");
                return;
            }

            // Queue it the way the stock part menu does, so it runs in the safe
            // window and goes through Vehicle.Split with this client as the
            // owner - which is what replicates the undock to everyone else.
            Log($"UNDOCK ALLOW #{p.RequestId}: undocking {stack.Id} at port #{p.ConnectorIndex} " +
                $"for {p.Requester}");
            InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData
            {
                Vehicle = stack,
                DockingPort = port,
                Undock = true
            });

            AnswerTo(p, UndockRequestMessage.STATUS_ACCEPTED, string.Empty);
            Notify($"Undocking {p.Requester}.");
        }

        /// <summary>Refuses the pending request.</summary>
        public static void Decline()
        {
            Prompt? p = Pending;
            Pending = null;
            if (p == null) return;

            Log($"UNDOCK DECLINED #{p.RequestId}: refused {p.Requester}");
            AnswerTo(p, UndockRequestMessage.STATUS_DECLINED, $"{LocalPlayerName} declined");
        }

        private static void Answer(UndockRequestMessage msg, byte status, string reason)
        {
            Log($"UNDOCK ANSWER #{msg.RequestId}: {(status == UndockRequestMessage.STATUS_ACCEPTED ? "accepted" : "declined")}" +
                $"{(string.IsNullOrEmpty(reason) ? "" : " - " + reason)}");

            OnSend?.Invoke(new UndockRequestMessage
            {
                Status = status,
                RequesterPlayerName = msg.RequesterPlayerName,
                OwnerPlayerName = LocalPlayerName,
                StackUid = msg.StackUid,
                ConnectorKind = msg.ConnectorKind,
                ConnectorIndex = msg.ConnectorIndex,
                RequestId = msg.RequestId,
                Reason = reason
            });
        }

        private static void AnswerTo(Prompt p, byte status, string reason)
        {
            OnSend?.Invoke(new UndockRequestMessage
            {
                Status = status,
                RequesterPlayerName = p.Requester,
                OwnerPlayerName = LocalPlayerName,
                StackUid = p.StackUid,
                ConnectorKind = p.ConnectorKind,
                ConnectorIndex = p.ConnectorIndex,
                RequestId = p.RequestId,
                Reason = reason
            });
        }

        // ------------------------------------------------- passenger, answered

        private static void HandleAnswer(UndockRequestMessage msg)
        {
            Outgoing? o = _outgoing;
            if (o == null) return;

            // Only the ask this client is actually waiting on.
            if (msg.RequestId != o.RequestId ||
                !string.Equals(msg.RequesterPlayerName, LocalPlayerName, StringComparison.Ordinal))
            {
                return;
            }

            _outgoing = null;

            if (msg.Status == UndockRequestMessage.STATUS_ACCEPTED)
            {
                Log($"UNDOCK ACCEPTED #{msg.RequestId} by {msg.OwnerPlayerName}");
                Notify($"{msg.OwnerPlayerName} is undocking you.");
                return;
            }

            string why = string.IsNullOrWhiteSpace(msg.Reason) ? "declined" : msg.Reason;
            Log($"UNDOCK REFUSED #{msg.RequestId} by {msg.OwnerPlayerName}: {why}");
            Notify($"Undock refused: {why}.");
        }

        // -------------------------------------------------------------- ticking

        /// <summary>Expires an ask nobody answered and a prompt nobody read.</summary>
        public static void Update()
        {
            DateTime now = DateTime.UtcNow;

            Prompt? p = Pending;
            if (p != null && now >= p.Deadline)
            {
                Pending = null;
                Log($"UNDOCK LAPSED #{p.RequestId}: {p.Requester} was not answered in time");
                AnswerTo(p, UndockRequestMessage.STATUS_DECLINED,
                    $"{LocalPlayerName} did not answer in time");
            }

            Outgoing? o = _outgoing;
            if (o != null && now >= o.Deadline)
            {
                _outgoing = null;
                Log($"UNDOCK ASK #{o.RequestId} timed out waiting on {o.Owner}");
                Notify($"{o.Owner} did not answer the undock.");
            }
        }

        private static void Notify(string text)
        {
            try
            {
                MultiplayerManager.Instance?.ChatManager?.AddSystemMessage(text);
            }
            catch { }
        }
    }
}
