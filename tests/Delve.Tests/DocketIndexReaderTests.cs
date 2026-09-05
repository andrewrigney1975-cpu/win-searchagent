using Delve.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Delve.Tests;

/// Builds a throwaway SQLite file matching Docket's exact SearchIndexService schema
/// (src/FileExplorer/Services/SearchIndexService.cs in the winui3-fileexplorer repo) and
/// exercises DocketIndexReader against it, standing in for the real search-index.db a running
/// Docket instance would maintain.
public sealed class DocketIndexReaderTests : IDisposable
{
    private readonly string _dbPath;

    public DocketIndexReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"delve-test-{Guid.NewGuid():N}.db");
        CreateSchema(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SearchAsync_EmptyDatabase_ReturnsNoResults()
    {
        var reader = new DocketIndexReader(_dbPath);
        var results = await reader.SearchAsync("anything", 10, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_SubstringMatch_ReturnsEntry()
    {
        Insert(@"C:\Data\readme.txt", "readme.txt", @"C:\Data", isDirectory: false);

        var reader = new DocketIndexReader(_dbPath);
        var results = await reader.SearchAsync("read", 10, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(@"C:\Data\readme.txt", results[0].Path);
    }

    /// FuzzyMatcher's own non-contiguous subsequence fallback (see FuzzyMatcherTests) is, in
    /// practice, unreachable through DocketIndexReader/Docket's own SearchIndexService: the SQL
    /// "WHERE Name LIKE '%query%'" pre-filter already requires query to appear as a literal
    /// contiguous substring, so anything FuzzyMatcher.TryScore would even see has already
    /// satisfied FuzzyMatcher's own fast substring path. A true out-of-order typo (e.g. "rdme"
    /// against "readme.txt", no contiguous substring) never reaches FuzzyMatcher at all here -
    /// this documents that inherited limitation rather than testing a scenario that can't work.
    [Fact]
    public async Task SearchAsync_NonContiguousTypo_IsFilteredOutBeforeFuzzyMatcherEverSeesIt()
    {
        Insert(@"C:\Data\readme.txt", "readme.txt", @"C:\Data", isDirectory: false);

        var reader = new DocketIndexReader(_dbPath);
        var results = await reader.SearchAsync("rdme", 10, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_RanksEarlierSubstringMatchFirst()
    {
        Insert(@"C:\Data\my-report.txt", "my-report.txt", @"C:\Data", isDirectory: false);
        Insert(@"C:\Data\report.txt", "report.txt", @"C:\Data", isDirectory: false);

        var reader = new DocketIndexReader(_dbPath);
        var results = await reader.SearchAsync("report", 10, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(@"C:\Data\report.txt", results[0].Path);
    }

    [Fact]
    public void TryGetEntryCount_ReflectsInsertedRows()
    {
        Insert(@"C:\Data\a.txt", "a.txt", @"C:\Data", isDirectory: false);
        Insert(@"C:\Data\b.txt", "b.txt", @"C:\Data", isDirectory: false);

        var reader = new DocketIndexReader(_dbPath);
        Assert.Equal(2, reader.TryGetEntryCount());
    }

    [Fact]
    public void TryGetEntryCount_MissingFile_ReturnsZeroRatherThanThrowing()
    {
        var reader = new DocketIndexReader(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.db"));
        Assert.Equal(0, reader.TryGetEntryCount());
    }

    [Fact]
    public async Task SearchAsync_WhileWriterHoldsOpenTransaction_StillReads()
    {
        // Simulates Docket's own SearchIndexService actively indexing (an open write
        // transaction) while Delve's read-only connection queries concurrently - WAL mode is
        // supposed to allow exactly this.
        Insert(@"C:\Data\readme.txt", "readme.txt", @"C:\Data", isDirectory: false);

        using var writer = new SqliteConnection($"Data Source={_dbPath}");
        writer.Open();
        using var transaction = writer.BeginTransaction();
        using (var cmd = writer.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO Entries (Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks, RootPath, ScanGeneration) VALUES ('C:\\Data\\other.txt','other.txt','C:\\Data',0,0,0,'C:\\Data',1)";
            cmd.ExecuteNonQuery();
        }

        var reader = new DocketIndexReader(_dbPath);
        var results = await reader.SearchAsync("readme", 10, CancellationToken.None);

        Assert.Single(results);
        transaction.Rollback();
    }

    private void Insert(string path, string name, string directory, bool isDirectory)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Entries (Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks, RootPath, ScanGeneration)
            VALUES (@path, @name, @dir, @isDir, 0, 0, @dir, 1)
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@dir", directory);
        cmd.Parameters.AddWithValue("@isDir", isDirectory ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void CreateSchema(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Entries (
                Path TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                DirectoryPath TEXT NOT NULL,
                IsDirectory INTEGER NOT NULL,
                SizeBytes INTEGER NOT NULL,
                ModifiedTicks INTEGER NOT NULL,
                RootPath TEXT NOT NULL,
                ScanGeneration INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Entries_Name ON Entries(Name);
            CREATE INDEX IF NOT EXISTS IX_Entries_RootPath ON Entries(RootPath);
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
}
