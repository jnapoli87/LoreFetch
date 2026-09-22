using LoreFetch.Core.Imaging;
using Xunit;

namespace LoreFetch.Tests.StreamB;

public class CardHashTests
{
    [Fact]
    public void HammingDistance_IdenticalHashes_IsZero()
    {
        var words = MakeWords(0x1234_5678_9ABC_DEF0UL);
        var a = new CardHash(words);
        var b = new CardHash(words);

        Assert.Equal(0, a.HammingDistance(b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void HammingDistance_CountsBitsAcrossAllSixteenWords_NotJustOne()
    {
        var zero = new CardHash(MakeWords(0UL));

        // One differing bit in each of the 16 words -- a partial/early-exit
        // distance that only looked at word 0 would report 1, not 16.
        var words = MakeWords(0UL);
        for (var i = 0; i < CardHash.WordCount; i++)
        {
            words[i] = 1UL << i;
        }

        var scattered = new CardHash(words);

        Assert.Equal(CardHash.WordCount, zero.HammingDistance(scattered));
    }

    [Fact]
    public void HammingDistance_FullyDifferentWords_Is1024()
    {
        var allZero = new CardHash(MakeWords(0UL));
        var allOnes = new CardHash(MakeWords(ulong.MaxValue));

        Assert.Equal(CardHash.BitCount, allZero.HammingDistance(allOnes));
        Assert.Equal(1024, CardHash.BitCount);
    }

    [Fact]
    public void Constructor_WrongWordCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CardHash(new ulong[15]));
        Assert.Throws<ArgumentException>(() => new CardHash(new ulong[17]));
    }

    [Fact]
    public void Default_IsUninitialized_AndNotUsable()
    {
        var uninitialized = default(CardHash);

        Assert.Throws<InvalidOperationException>(() => uninitialized.HammingDistance(uninitialized));
        Assert.Throws<InvalidOperationException>(() => _ = uninitialized.Words.Length);
    }

    [Fact]
    public void ToString_IsTwoHundredFiftySixLowercaseHexChars()
    {
        var hash = new CardHash(MakeWords(0xDEADBEEF_CAFEBABEUL));
        var text = hash.ToString();

        Assert.Equal(256, text.Length);
        Assert.Equal(text, text.ToLowerInvariant());
        Assert.True(text.All(c => Uri.IsHexDigit(c)));
    }

    private static ulong[] MakeWords(ulong fill)
    {
        var words = new ulong[CardHash.WordCount];
        Array.Fill(words, fill);
        return words;
    }
}
