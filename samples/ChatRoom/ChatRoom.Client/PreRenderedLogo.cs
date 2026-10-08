using System.IO.Compression;
using Spectre.Console;
using Spectre.Console.Rendering;

internal sealed class PreRenderedLogo : Renderable
{
    private const int PixelWidth = 2;
    private const int MaxWidth = 25 * PixelWidth;
    // Entries cover 1-25 pixels, with two console cells per pixel.
    private readonly string[] _markup;

    public PreRenderedLogo()
    {
        using var stream = typeof(PreRenderedLogo).Assembly.GetManifestResourceStream("ChatRoom.Client.logo.markup.gz");
        using var compressed = new GZipStream(stream!, CompressionMode.Decompress);
        using var reader = new StreamReader(compressed);
        _markup = reader.ReadToEnd().Split('\f');
    }

    protected override Measurement Measure(RenderOptions options, int maxWidth)
    {
        var width = Math.Min(maxWidth, MaxWidth);
        return new Measurement(width, width);
    }

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        ((IRenderable)new Markup(_markup[Math.Min(maxWidth, MaxWidth) / PixelWidth - 1])).Render(options, maxWidth);
}
