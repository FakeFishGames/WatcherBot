using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

// ReSharper disable CollectionNeverUpdated.Local

namespace WatcherBot.Config;

public class Spam
{
    private readonly Lazy<IReadOnlySet<(string Substring, int MaxDistance, float Weight)>> spam;

    public Spam()
    {
        spam = new Lazy<IReadOnlySet<(string Substring, int MaxDistance, float Weight)>>(() =>
            spamSubstrings.Zip(spamSubstringMaxDist)
                          .Zip(spamSubstringWeights, (tup, dist) => (tup.First, tup.Second, fl: dist))
                          .ToImmutableHashSet());

        SpamCharsConverted = new Lazy<HashSet<char>>(() =>
        {
            HashSet<char> set = new();
            foreach (string value in spamChars.Select(static c => c.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? c[2..] : c))
            {
                if (ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var converted))
                {
                    set.Add((char)converted);
                }
            }

            return set;
        });
    }

    private List<string> spamSubstrings { get; } = new();
    private List<float> spamSubstringWeights { get; } = new();
    private List<int> spamSubstringMaxDist { get; } = new();
    private HashSet<string> spamChars { get; } = new();
    private readonly Lazy<HashSet<char>> SpamCharsConverted;
    public IReadOnlySet<(string Substring, int MaxDistance, float Weight)> SpamSubstrings => spam.Value;
    public IReadOnlySet<char> SpamChars => SpamCharsConverted.Value;
}
