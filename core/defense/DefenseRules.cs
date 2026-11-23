using ScopeCreep.Core.enums;

namespace ScopeCreep.Core.defense;



/**
 *  The timing rules of the dodge & parry quick time event.
 *  Every boss attack has an impact moment; the player presses the target hero's key around it.
 *  All tuning numbers for the event live here.
 */
public static class DefenseRules
{
    /** How far from the impact (in seconds, either side) a press still counts as a parry. */
    public const double PARRY_WINDOW = 0.035;

    /** How far from the impact (in seconds, either side) a press still counts as a dodge. */
    public const double DODGE_WINDOW = 0.12;

    /** The boss winds up for a random time before throwing, so the timing can't be memorized. */
    public const double MIN_TELL = 0.15;
    public const double MAX_TELL = 0.5;

    /** How long the projectile flies, from leaving the boss to the impact. Always the same, so the player learns to read it. */
    public const double FLIGHT_TIME = 0.35;

    /** The counter a parry gives: Spirit Points added to the shared pool. */
    public const int PARRY_SP_GAIN = 1;


    /**
     *  Judge a key press by how far it was from the impact, in seconds (negative = early, positive = late).
     */
    public static DefenseResult judge(double press_offset)
    {
        double distance = Math.Abs(press_offset);

        if (distance <= PARRY_WINDOW) return DefenseResult.PARRY;
        if (distance <= DODGE_WINDOW) return DefenseResult.DODGE;

        return DefenseResult.HIT;
    }


    /**
     *  Pick a random wind-up time before the boss throws.
     */
    public static double rollTell(Random rng) => MIN_TELL + rng.NextDouble() * (MAX_TELL - MIN_TELL);
}
