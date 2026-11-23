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
    public const double PARRY_WINDOW = 0.05;

    /** How far from the impact (in seconds, either side) a press still counts as a dodge. */
    public const double DODGE_WINDOW = 0.20;

    /** The shortest and longest time between the boss attacking and the impact, so the timing can't be memorized. */
    public const double MIN_WINDUP = 0.7;
    public const double MAX_WINDUP = 1.3;

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
     *  Pick a random time between the boss attacking and the impact.
     */
    public static double rollWindup(Random rng) => MIN_WINDUP + rng.NextDouble() * (MAX_WINDUP - MIN_WINDUP);
}
