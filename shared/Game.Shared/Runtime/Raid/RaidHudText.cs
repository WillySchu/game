using System;
using System.Globalization;

namespace RaidZero.Shared.Raid
{
    /// <summary>The words and numbers on the raid HUD, apart from Unity so they're tested.</summary>
    public static class RaidHudText
    {
        /// <summary>
        /// The raid clock, "18:42": whole seconds rounded up, so it reads 0:00 only when time is really up; hours
        /// when a raid is that long ("1:05:00").
        /// </summary>
        public static string Clock(double secondsRemaining)
        {
            var total = (long)Math.Ceiling(Math.Max(0, secondsRemaining));
            var hours = total / 3600;
            var minutes = total / 60 % 60;
            var seconds = total % 60;
            return hours > 0
                ? hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + seconds.ToString("00", CultureInfo.InvariantCulture)
                : minutes.ToString(CultureInfo.InvariantCulture) + ":" + seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>How far through the extraction countdown, 0 at the start to 1 when it completes.</summary>
        public static float ExtractionProgress(double secondsLeft, double totalSeconds)
        {
            if (totalSeconds <= 0)
            {
                return 1f;
            }

            var done = 1 - secondsLeft / totalSeconds;
            return (float)Math.Min(1, Math.Max(0, done));
        }

        /// <summary>"Extracting at north gate: 5", with the point's underscores as spaces.</summary>
        public static string ExtractionLine(string point, double secondsLeft) =>
            "Extracting" + (string.IsNullOrEmpty(point) ? "" : " at " + PointName(point)) + ": "
            + ((long)Math.Ceiling(Math.Max(0, secondsLeft))).ToString(CultureInfo.InvariantCulture);

        /// <summary>The card a player sees as they leave a raid, before the Hideout: a title and a line under it.</summary>
        public static (string Title, string Detail) ExitCard(RaidExit exit, string extractPoint)
        {
            switch (exit)
            {
                case RaidExit.Extracted:
                    return ("Extracted", string.IsNullOrEmpty(extractPoint) ? "You made it out." : "You made it out at " + PointName(extractPoint) + ".");
                case RaidExit.Died:
                    return ("Killed", "Everything you carried stays where you fell.");
                case RaidExit.MissingInAction:
                    return ("Missing in action", "You were still inside when the raid ended. Your gear stays in the zone.");
                case RaidExit.Aborted:
                    return ("Raid called off", "The raid ended before it could count. Your gear is coming back.");
                default:
                    return ("Out of the raid", "");
            }
        }

        private static string PointName(string point) => point.Replace('_', ' ');
    }
}
