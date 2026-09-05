using Delve.Helpers;
using Delve.Models;
using Microsoft.Data.Sqlite;

namespace Delve.Services;

/// Reads Docket's "Search Everywhere" SQLite index directly and read-only. Docket
/// (src/FileExplorer/Services/SearchIndexService.cs) opens the same file in WAL mode
/// (PRAGMA journal_mode=WAL), which explicitly supports concurrent external readers while
/// Docket's own process holds the writer connection - so this needs no changes to Docket and
/// no IPC of any kind, just a second reader on the same file.
///
/// Deliberately not shared code with Docket: Delve is a small standalone tray app and
/// shouldn't take a project/package reference on Docket's much larger WinUI dependency graph
/// just for this. Kept schema-compatible by hand - see EnsureCompatibleSchema.
public sealed class DocketIndexReader
{
    // Mirrors SearchIndexService.SqlCandidateLimit - the ceiling on rows pulled from SQLite
    // before fuzzy-ranking in memory, so a broad query on a multi-million-row index doesn't
    // page the whole match set into memory just to keep the top N.
    private const int SqlCandidateLimit = 2000;

    private readonly string _dbPath;

    public DocketIndexReader(string dbPath)
    {
        _dbPath = dbPath;
    }

    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileExplorerApp",
        "search-index.db");

    public bool IndexFileExists => File.Exists(_dbPath);

    /// Cheap "is there anything to search" check - also doubles as a schema/connectivity probe
    /// so a corrupt or foreign search-index.db reads as "no rows" rather than throwing.
    public int TryGetEntryCount()
    {
        try
        {
            using var connection = OpenReadOnlyConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Entries";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// Same shape as Docket's SearchIndexService.SearchAsync: a SQL substring pre-filter
    /// (index-backed on Name), then FuzzyMatcher ranking in memory. Deliberately omits
    /// Docket's optional rating filter/sort - that reads a second, separate JSON-backed store
    /// (RatingService) that isn't part of the read-only contract this class relies on.
    public async Task<List<SearchResultItem>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<SearchResultItem>();
        }

        return await Task.Run(() =>
        {
            var candidates = new List<SearchResultItem>();

            using (var connection = OpenReadOnlyConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks FROM Entries WHERE Name LIKE @pattern ESCAPE '\\' LIMIT @limit";
                cmd.Parameters.AddWithValue("@pattern", "%" + EscapeLike(query) + "%");
                cmd.Parameters.AddWithValue("@limit", SqlCandidateLimit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidates.Add(new SearchResultItem(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt64(3) != 0, reader.GetInt64(4),
                        new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)));
                }
            }

            var scored = new List<(SearchResultItem Entry, int Score)>();
            foreach (var candidate in candidates)
            {
                if (FuzzyMatcher.TryScore(candidate.Name, query, out var score))
                {
                    scored.Add((candidate, score));
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Select(s => s.Entry)
                .Take(maxResults)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// "Mode=ReadOnly" refuses to create the file if missing and never opens a write
    /// transaction - this reader can never corrupt or contend for Docket's own writes.
    private SqliteConnection OpenReadOnlyConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
