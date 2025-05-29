using System;
using System.Collections.Generic;

namespace Orleans.Caching.Internal;

// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/ConcurrentDictionarySize.cs
internal static class ConcurrentDictionarySize
{
    private static int NextPrimeGreaterThan(int min)
    {
        foreach (var prime in Primes)
        {
            if (prime > min)
            {
                return prime;
            }
        }

        return min;
    }

    /// <summary>
    /// Estimate the size of the ConcurrentDictionary constructor capacity arg to use for the given desired cache size.
    /// </summary>
    /// <remarks>
    /// To minimize collisions, ideal case is is for ConcurrentDictionary to have a prime number of buckets, and
    /// for the bucket count to be about 33% greater than the cache capacity (load factor of 0.75).
    /// See load factor here: https://en.wikipedia.org/wiki/Hash_table
    /// </remarks>
    /// <param name="desiredSize">The desired cache size</param>
    /// <returns>The estimated optimal ConcurrentDictionary capacity</returns>
    internal static int Estimate(int desiredSize)
    {
        // Size map entries are approx 4% apart in the worst case, so increase by 29% to target 33%.
        // In practice, this leads to the number of buckets being somewhere between 29% and 40% greater
        // than cache capacity.
        try
        {
            checked
            {
                desiredSize = (int)(desiredSize * 1.29);
            }

            // When small, exact size hashtable to nearest larger prime number
            if (desiredSize < 197)
            {
                return NextPrimeGreaterThan(desiredSize);
            }

            // When large, size to approx 10% of desired size to save memory. Initial value is chosen such
            // that 4x ConcurrentDictionary grow operations will select a prime number slightly larger
            // than desired size.
            foreach (var pair in SizeMap)
            {
                if (pair.Key > desiredSize)
                {
                    return pair.Value;
                }
            }
        }
        catch (OverflowException)
        {
            // return largest
        }

        // Use largest mapping: ConcurrentDictionary will resize to max array size after 4x grow calls.
        return SizeMap[^1].Value;
    }

#if NETSTANDARD2_0
    internal static int[] Primes = new int[] {
#else
    private static ReadOnlySpan<int> Primes => new int[] {
#endif
        3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
        1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
        17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
        187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
        1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369
    };

#if NETSTANDARD2_0
    internal static KeyValuePair<int, int>[] SizeMap =
#else
    private static ReadOnlySpan<KeyValuePair<int, int>> SizeMap =>
#endif
        new KeyValuePair<int, int>[129]
        {
            new(197, 197),
            new(277, 137),
            new(331, 163),
            new(359, 179),
            new(397, 197),
            new(443, 221),
            new(499, 247),
            new(557, 137),
            new(599, 149),
            new(677, 167),
            new(719, 179),
            new(797, 197),
            new(839, 209),
            new(887, 221),
            new(1061, 131),
            new(1117, 137),
            new(1237, 151),
            new(1439, 179),
            new(1559, 193),
            new(1777, 221),
            new(2011, 247),
            new(2179, 269),
            new(2347, 289),
            new(2683, 331),
            new(2797, 347),
            new(3359, 419),
            new(3917, 487),
            new(4363, 541),
            new(4597, 571),
            new(5879, 733),
            new(7517, 937),
            new(8731, 1087),
            new(9839, 1229),
            new(17467, 2179),
            new(18397, 2297),
            new(20357, 2543),
            new(24317, 3037),
            new(25919, 3239),
            new(29759, 3719),
            new(31357, 3917),
            new(33599, 4199),
            new(38737, 4841),
            new(41117, 5137),
            new(48817, 6101),
            new(61819, 7723),
            new(72959, 9119),
            new(86011, 10747),
            new(129277, 16157),
            new(140797, 17597),
            new(164477, 20557),
            new(220411, 27547),
            new(233851, 29227),
            new(294397, 36797),
            new(314879, 39359),
            new(338683, 42331),
            new(389117, 48637),
            new(409597, 51197),
            new(436477, 54557),
            new(609277, 76157),
            new(651517, 81437),
            new(737279, 92159),
            new(849917, 106237),
            new(1118203, 139771),
            new(1269757, 158717),
            new(1440763, 180091),
            new(1576957, 197117),
            new(1684477, 210557),
            new(2293757, 286717),
            new(2544637, 318077),
            new(2666491, 333307),
            new(2846717, 355837),
            new(3368957, 421117),
            new(3543037, 442877),
            new(4472827, 559099),
            new(4710397, 588797),
            new(5038079, 629759),
            new(5763067, 720379),
            new(6072317, 759037),
            new(6594557, 824317),
            new(7913467, 989179),
            new(8257531, 1032187),
            new(9175037, 1146877),
            new(9633787, 1204219),
            new(10076159, 1259519),
            new(11386877, 1423357),
            new(14020603, 1752571),
            new(16056317, 2007037),
            new(19496957, 2437117),
            new(20848637, 2606077),
            new(24084479, 3010559),
            new(27934717, 3491837),
            new(29589499, 3698683),
            new(32788477, 4098557),
            new(36044797, 4505597),
            new(38051837, 4756477),
            new(43581437, 5447677),
            new(51814397, 6476797),
            new(56688637, 7086077),
            new(60948479, 7618559),
            new(69631997, 8703997),
            new(75366397, 9420797),
            new(78643199, 9830399),
            new(96337919, 12042239),
            new(106168319, 13271039),
            new(115671037, 14458877),
            new(132382717, 16547837),
            new(144179197, 18022397),
            new(165150719, 20643839),
            new(178257917, 22282237),
            new(188743679, 23592959),
            new(209715197, 26214397),
            new(254279677, 31784957),
            new(297271291, 37158907),
            new(314572799, 39321599),
            new(385351679, 48168959),
            new(453509117, 56688637),
            new(517472251, 64684027),
            new(644874239, 80609279),
            new(673710077, 84213757),
            new(770703359, 96337919),
            new(849346559, 106168319),
            new(903086077, 112885757),
            new(1145044987, 143130619),
            new(1233125371, 154140667),
            new(1321205759, 165150719),
            new(1394606077, 174325757),
            new(1635778559, 204472319),
            new(1855979519, 231997439),
            new(2003828731, 250478587),
        };
}
