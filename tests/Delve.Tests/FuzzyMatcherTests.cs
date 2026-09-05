using Delve.Helpers;
using Xunit;

namespace Delve.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void ExactSubstring_Matches_AndOutscoresLaterSubstring()
    {
        Assert.True(FuzzyMatcher.TryScore("readme.txt", "read", out var early));
        Assert.True(FuzzyMatcher.TryScore("my-readme.txt", "read", out var late));
        Assert.True(early > late);
    }

    [Fact]
    public void TypoTolerantSubsequence_Matches()
    {
        Assert.True(FuzzyMatcher.TryScore("readme.txt", "rdme", out _));
    }

    [Fact]
    public void NoMatch_ReturnsFalse()
    {
        Assert.False(FuzzyMatcher.TryScore("readme.txt", "zzz", out _));
    }

    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.True(FuzzyMatcher.TryScore("anything.txt", "", out var score));
        Assert.Equal(0, score);
    }

    [Fact]
    public void EmptyText_NeverMatchesNonEmptyQuery()
    {
        Assert.False(FuzzyMatcher.TryScore("", "a", out _));
    }
}
