namespace Citus.Ui.Shared.Pages;

public sealed record class PageHeaderModel
{
    public string Eyebrow { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Subtitle { get; init; } = string.Empty;
}
