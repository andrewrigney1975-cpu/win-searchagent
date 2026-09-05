using Microsoft.UI.Xaml.Media;

namespace Delve.Models;

/// Flattens a SearchResultItem plus its resolved shell icon into the shape the results
/// ListView binds against - kept separate from SearchResultItem so DocketIndexReader (a pure
/// data-access class) never needs to know about WinUI's ImageSource type.
public sealed class SearchResultViewModel
{
    public SearchResultViewModel(SearchResultItem item, ImageSource? icon)
    {
        Item = item;
        Icon = icon;
    }

    public SearchResultItem Item { get; }
    public ImageSource? Icon { get; }

    public string Name => Item.Name;
    public string DirectoryPath => Item.DirectoryPath;
    public string Path => Item.Path;
    public bool IsDirectory => Item.IsDirectory;
}
