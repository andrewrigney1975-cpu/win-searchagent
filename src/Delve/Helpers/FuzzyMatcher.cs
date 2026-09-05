namespace Delve.Helpers;

/// Vendored, byte-identical copy of Docket's FileExplorer.Helpers.FuzzyMatcher
/// (src/FileExplorer/Helpers/FuzzyMatcher.cs in the winui3-fileexplorer repo), so Delve's
/// result ranking feels the same as Docket's own Search Everywhere. Delve deliberately doesn't
/// take a project/package reference on Docket (a much larger WinUI app) just for this ~60-line
/// algorithm - keep this in sync by hand if Docket's version changes.
///
/// Typo-tolerant matching: an exact substring scores highest (bonus for matching near the
/// start), otherwise falls back to an in-order subsequence match (e.g. "rdme" matches
/// "readme.txt") scored by how consecutive the matched characters are.
public static class FuzzyMatcher
{
    public static bool TryScore(string text, string query, out int score)
    {
        score = 0;

        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var substringIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (substringIndex >= 0)
        {
            score = 10000 - substringIndex;
            return true;
        }

        var textIndex = 0;
        var lastMatchIndex = -1;
        var consecutiveRun = 0;
        var runBonus = 0;

        foreach (var queryChar in query)
        {
            var found = -1;
            for (var i = textIndex; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(queryChar))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            consecutiveRun = found == lastMatchIndex + 1 ? consecutiveRun + 1 : 0;
            runBonus += consecutiveRun;
            lastMatchIndex = found;
            textIndex = found + 1;
        }

        score = runBonus + 1;
        return true;
    }
}
