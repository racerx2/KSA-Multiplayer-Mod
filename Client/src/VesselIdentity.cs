using System;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Builds and parses creator-based vessel identity strings.</summary>
    public static class VesselIdentity
    {
        public const char Separator = '|';
        private const string RemotePrefix = "MP|";

        /// <summary>Identity for a vessel created locally by this player.</summary>
        public static string MakeUid(string creator, string localId)
            => $"{creator}{Separator}{localId}";

        /// <summary>Splits a uid into creator and local id at the first separator.</summary>
        public static bool TryParseUid(string uid, out string creator, out string localId)
        {
            creator = string.Empty;
            localId = string.Empty;
            if (string.IsNullOrEmpty(uid)) return false;

            int i = uid.IndexOf(Separator);
            if (i <= 0 || i == uid.Length - 1) return false;

            creator = uid.Substring(0, i);
            localId = uid.Substring(i + 1);
            return true;
        }

        /// <summary>Returns the name this vessel carries in the local universe.</summary>
        public static string LocalNameFor(string uid, string localPlayerName)
        {
            if (!TryParseUid(uid, out string creator, out string localId))
                return uid;

            // Locally created vessels keep their plain game name.
            return string.Equals(creator, localPlayerName, StringComparison.Ordinal)
                ? localId
                : RemotePrefix + uid;
        }

        /// <summary>True if this local name refers to a vessel created by another player.</summary>
        public static bool IsRemoteName(string localName)
            => !string.IsNullOrEmpty(localName)
               && localName.StartsWith(RemotePrefix, StringComparison.Ordinal);

        /// <summary>Recover the uid from a local name, whoever created the vessel.</summary>
        public static string UidFromLocalName(string localName, string localPlayerName)
            => IsRemoteName(localName)
                ? localName.Substring(RemotePrefix.Length)
                : MakeUid(localPlayerName, localName);

        /// <summary>Returns the wire uid, falling back to owner and vehicle id when absent.</summary>
        public static string UidFromWire(string uid, string ownerPlayerName, string vehicleId)
            => string.IsNullOrEmpty(uid) ? MakeUid(ownerPlayerName, vehicleId) : uid;
    }
}
