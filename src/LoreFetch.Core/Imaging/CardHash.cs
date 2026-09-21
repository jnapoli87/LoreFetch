using System.Numerics;
using System.Text;

namespace LoreFetch.Core.Imaging;

/// The 1024-bit perceptual hash `CardHasher` produces: 16 grid cells (a 4x4
/// grid of 8x8-pixel cells), one `ulong` per cell. Bit `i` of a cell's word
/// is the raster-order pixel at `(i / 8, i % 8)` within that 8x8 cell
/// (row-major, LSB first); cells themselves are enumerated row-major into
/// `Words[0..15]` (index = `gridRow * 4 + gridCol`).
///
/// Matching is always the FULL Hamming distance across all 16 words -- see
/// CLAUDE.md "step 7: do not port upstream's early rejection." CardSpotter's
/// own early rejection is threshold-keyed (it prunes on one cell's distance
/// against a fraction of the match-score threshold) and can discard the true
/// best match, so this type deliberately has no partial-distance shortcut.
public readonly struct CardHash : IEquatable<CardHash>
{
    /// 16 grid cells (4x4), 64 bits each = 1024 bits.
    public const int WordCount = 16;
    public const int BitCount = WordCount * 64;

    private readonly ulong[]? _words;

    public CardHash(ReadOnlySpan<ulong> words)
    {
        if (words.Length != WordCount)
        {
            throw new ArgumentException($"CardHash needs exactly {WordCount} words, got {words.Length}.", nameof(words));
        }

        _words = words.ToArray();
    }

    /// The 16 grid-cell words, cell `(gridRow, gridCol)` at index
    /// `gridRow * 4 + gridCol`. Throws for `default(CardHash)` -- there is no
    /// meaningful all-zero hash, so an uninitialized value is never usable.
    public ReadOnlySpan<ulong> Words
    {
        get
        {
            RequireInitialized();
            return _words;
        }
    }

    /// Full 1024-bit Hamming distance -- every word, always, never an early
    /// exit. See the type's own doc comment for why.
    public int HammingDistance(CardHash other)
    {
        RequireInitialized();
        other.RequireInitialized();

        var distance = 0;
        for (var i = 0; i < WordCount; i++)
        {
            distance += BitOperations.PopCount(_words![i] ^ other._words![i]);
        }

        return distance;
    }

    public bool Equals(CardHash other)
    {
        if (_words is null || other._words is null)
        {
            return ReferenceEquals(_words, other._words);
        }

        return ((ReadOnlySpan<ulong>)_words).SequenceEqual(other._words);
    }

    public override bool Equals(object? obj) => obj is CardHash other && Equals(other);

    public override int GetHashCode()
    {
        if (_words is null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var word in _words)
        {
            hash.Add(word);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(CardHash left, CardHash right) => left.Equals(right);

    public static bool operator !=(CardHash left, CardHash right) => !left.Equals(right);

    /// Lowercase hex, 16 chars per word x 16 words = 256 hex chars -- the
    /// form the (later) golden-hash tests pin literal expected values
    /// against.
    public override string ToString()
    {
        RequireInitialized();

        var chars = new StringBuilder(WordCount * 16);
        foreach (var word in _words!)
        {
            chars.Append(word.ToString("x16"));
        }

        return chars.ToString();
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_words))]
    private void RequireInitialized()
    {
        if (_words is null)
        {
            throw new InvalidOperationException("CardHash is uninitialized (default(CardHash) is not a usable value).");
        }
    }
}
