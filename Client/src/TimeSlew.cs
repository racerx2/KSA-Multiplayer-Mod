using System;
using KSA;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Converges the local simulation clock onto the furthest-ahead player by adjusting the simulation rate.</summary>
    public class TimeSlew
    {
        private const string LogName = "TimeSlew";

        /// <summary>Gap above which slewing engages.</summary>
        private const double EngageGapSeconds = 0.30;

        /// <summary>Gap below which slewing releases.</summary>
        private const double ReleaseGapSeconds = 0.08;

        /// <summary>Gap above which slewing is not used.</summary>
        private const double MaxSlewableGapSeconds = 120.0;

        /// <summary>Maximum simulation rate applied while slewing.</summary>
        private const double MaxSlewRate = 1.25;

        /// <summary>Target duration over which the gap is closed.</summary>
        private const double TargetCloseSeconds = 20.0;

        private readonly SubspaceManager _subspace;

        private bool _slewing;
        private double _appliedRate = 1.0;
        private double _lastLoggedGap;

        public bool IsSlewing => _slewing;
        public double CurrentGap { get; private set; }

        public TimeSlew(SubspaceManager subspace) => _subspace = subspace;

        private static void Log(string msg) => ModLogger.Log(LogName, msg);

        /// <summary>Measures the gap to the furthest-ahead player and adjusts the local rate.</summary>
        public void Update(bool connected)
        {
            // Catches all exceptions and releases on failure.
            try
            {
                UpdateCore(connected);
            }
            catch (Exception ex)
            {
                ModLogger.LogThrottled(LogName, "SLEW_ERR", $"Slew update failed: {ex.Message}");
                Release("error during update");
            }
        }

        private void UpdateCore(bool connected)
        {
            if (_subspace == null) return;

            if (!connected)
            {
                Release("disconnected");
                return;
            }

            // Skips while the player has set a simulation speed of their own.
            double currentSpeed = Universe.GetSimulationSpeed();
            if (!_slewing && Math.Abs(currentSpeed - 1.0) > 0.001)
            {
                CurrentGap = 0;
                return;
            }
            if (_slewing && Math.Abs(currentSpeed - _appliedRate) > 0.001)
            {
                // Releases when the simulation speed was changed elsewhere.
                Release("simulation speed changed externally");
                return;
            }

            double gap = GapToFurthestAhead();
            CurrentGap = gap;

            // Releases once the gap is at or below the release threshold.
            if (gap <= ReleaseGapSeconds)
            {
                Release(gap <= 0 ? "caught up" : $"closed to {gap:F2}s");
                return;
            }

            if (!_slewing && gap < EngageGapSeconds)
                return;   // Gap below the engage threshold.

            if (gap > MaxSlewableGapSeconds)
            {
                Release($"gap {gap:F1}s exceeds what slewing can close - use sync");
                return;
            }

            // Computes a capped rate proportional to the gap.
            double desiredExtra = gap / TargetCloseSeconds;
            double rate = Math.Min(1.0 + desiredExtra, MaxSlewRate);

            Universe.SetSimulationSpeed(rate, alert: false);
            _appliedRate = rate;

            if (!_slewing)
            {
                _slewing = true;
                _lastLoggedGap = gap;
                Log($"SLEW START: {gap:F2}s behind, running at {rate:F3}x");
            }
            else if (Math.Abs(gap - _lastLoggedGap) > 0.5)
            {
                _lastLoggedGap = gap;
                Log($"SLEW: {gap:F2}s remaining at {rate:F3}x");
            }
        }

        /// <summary>Returns how many seconds behind the furthest-ahead player the local clock is.</summary>
        private double GapToFurthestAhead()
        {
            double local = _subspace.GetLocalTime();
            double furthest = local;
            string? me = _subspace.LocalPlayerName;

            foreach (string player in _subspace.GetKnownPlayers())
            {
                // Skips the local player's own entry.
                if (me != null && player == me) continue;

                // Keeps the largest player time; stale readings return zero.
                double theirs = _subspace.GetPlayerTime(player);
                if (theirs > furthest) furthest = theirs;
            }

            return furthest - local;
        }

        private void Release(string reason)
        {
            if (!_slewing) return;

            _slewing = false;
            _appliedRate = 1.0;
            Universe.SetSimulationSpeed(1.0, alert: false);
            Log($"SLEW END: {reason}");
        }
    }
}
