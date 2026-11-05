namespace FetchIt.ViewModels;

/// <summary>One line of the preview grid. The grid only builds the lines on screen.</summary>
public sealed record PreviewRow(IReadOnlyList<PreviewCard> Cards)
{
    public static IReadOnlyList<PreviewRow> Split(IReadOnlyList<PreviewCard> cards, int columns)
        => cards.Chunk(Math.Max(1, columns)).Select(row => new PreviewRow(row)).ToList();
}
