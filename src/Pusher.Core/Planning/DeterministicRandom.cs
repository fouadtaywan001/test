namespace Pusher.Core.Planning;

/// <summary>
/// Small, self-contained PCG32 generator. System.Random's seeded sequence is not
/// guaranteed stable across runtimes; the plan requires (script, files, seed) →
/// identical calendar on every machine, so we own the algorithm.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;
    private readonly ulong _inc;

    public DeterministicRandom(ulong seed, ulong stream = 54u)
    {
        _state = 0;
        _inc = (stream << 1) | 1;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    /// <summary>Derive an independent generator for a sub-domain (e.g. one calendar day).</summary>
    public static DeterministicRandom ForDay(int seed, DateOnly day)
        => new((ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)day.DayNumber, (ulong)day.DayNumber | 1);

    public uint NextUInt()
    {
        ulong old = _state;
        _state = old * 6364136223846793005UL + _inc;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << (-rot & 31));
    }

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public int NextInt(int min, int max)
    {
        if (min >= max) return min;
        ulong range = (ulong)(max - min + 1);
        return min + (int)(NextUInt() % range);
    }

    /// <summary>Roll in [0, 100): true when below <paramref name="percent"/>.</summary>
    public bool Chance(int percent) => NextInt(0, 99) < percent;
}
