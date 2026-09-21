namespace LoreFetch.Core.Abstractions;

public readonly record struct PointF2(float X, float Y);

/// Corners in frame coordinates, always ordered TL, TR, BR, BL.
public readonly record struct CardQuad(PointF2 TL, PointF2 TR, PointF2 BR, PointF2 BL)
{
    /// Shoelace formula over the four corners, in px^2.
    public float AreaPx =>
        0.5f * MathF.Abs(
            (TL.X * TR.Y - TR.X * TL.Y) +
            (TR.X * BR.Y - BR.X * TR.Y) +
            (BR.X * BL.Y - BL.X * BR.Y) +
            (BL.X * TL.Y - TL.X * BL.Y));

    /// long/short, so a card reads ~1.397 regardless of orientation — never
    /// ~0.716, which is what short/long would give a landscape-oriented quad.
    public float AspectRatio
    {
        get
        {
            var topWidth = Distance(TL, TR);
            var bottomWidth = Distance(BL, BR);
            var leftHeight = Distance(TL, BL);
            var rightHeight = Distance(TR, BR);

            var width = (topWidth + bottomWidth) / 2f;
            var height = (leftHeight + rightHeight) / 2f;

            var longSide = MathF.Max(width, height);
            var shortSide = MathF.Min(width, height);

            return longSide / shortSide;
        }
    }

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}

public interface ICardDetector
{
    /// Returns at most maxCards quads, ordered by descending area.
    /// Returns empty when nothing card-shaped is present — never guesses.
    IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards);
}

/// A perspective-corrected single card at canonical size.
/// Plain managed memory — deliberately NOT pooled and NOT IDisposable.
public sealed class RectifiedCard
{
    public const int CanonicalWidth = 488; // matches Scryfall "normal"
    public const int CanonicalHeight = 680;

    public RectifiedCard(byte[] pixels, int stride, PixelLayout layout, CardQuad sourceQuad)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        Pixels = pixels;
        Stride = stride;
        Layout = layout;
        SourceQuad = sourceQuad;
    }

    public int Stride { get; }
    public PixelLayout Layout { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public CardQuad SourceQuad { get; }
}

public interface IRectifier
{
    /// Perspective-corrects `quad` out of `frame` into a canonical
    /// 488x680 `RectifiedCard`.
    RectifiedCard Rectify(CameraFrame frame, CardQuad quad);
}
