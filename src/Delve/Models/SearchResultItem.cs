namespace Delve.Models;

/// Mirrors Docket's SearchIndexService.SearchIndexEntry shape - Delve reads Docket's SQLite
/// index directly rather than calling into Docket, so this is an independent copy, not a
/// shared type.
public sealed record SearchResultItem(
    string Path,
    string Name,
    string DirectoryPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset Modified);
