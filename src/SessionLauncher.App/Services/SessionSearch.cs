using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SessionLauncher.App.Services
{
    /// <summary>One catalog row, reduced to what the search engine needs.</summary>
    public sealed record SearchDoc(string Id, string Title, string Directory, string Agent, int Messages);

    /// <summary>A UTF-16 character range inside a field, for highlighting.</summary>
    public readonly record struct Range(int Start, int Length);

    public sealed record SearchHit(SearchDoc Doc, double Score, IReadOnlyList<string> MatchedTokens);

    public sealed record SearchResult(
        SearchHit Hit,
        IReadOnlyList<Range> TitleRanges,
        IReadOnlyList<Range> DirectoryRanges,
        bool IsFuzzy);

    /// <summary>
    /// Everything-style instant search over the conversation catalog.
    /// </summary>
    /// <remarks>
    /// <para>Three ideas make it feel like Everything rather than a naive filter:</para>
    /// <list type="bullet">
    /// <item><description><b>AND across tokens</b> - every whitespace-separated token must
    /// match somewhere, so "gnss sample" narrows instead of widening.</description></item>
    /// <item><description><b>Ranked match quality</b> - exact beats word-prefix beats
    /// substring beats fuzzy subsequence, so typing more of a word floats that row upward
    /// instead of merely keeping it visible.</description></item>
    /// <item><description><b>Rune-aware subsequence</b> - matching walks Unicode scalar
    /// values, not UTF-16 units, so a CJK query works and a surrogate pair can never be
    /// half-matched.</description></item>
    /// </list>
    /// <para>Ordering is fully deterministic: score desc, then message count desc, then id
    /// ordinal. The list re-renders on every keystroke, so the same query must always
    /// produce the same order.</para>
    /// </remarks>
    public static class SessionSearch
    {
        // Match quality, highest first.
        private const double ScoreExact = 1000;
        private const double ScoreWordPrefix = 600;
        private const double ScoreSubstring = 400;
        private const double ScoreFuzzy = 200;

        // Field importance.
        private const double WeightTitle = 10;
        private const double WeightId = 8;
        private const double WeightDirectory = 6;
        private const double WeightAgent = 4;

        /// <summary>Reward each extra token that also matched, so precise queries rank first.</summary>
        private const double MultiTokenBonus = 1.5;

        private const int FieldNone = -1;
        private const int FieldTitle = 0;
        private const int FieldId = 1;
        private const int FieldDirectory = 2;
        private const int FieldAgent = 3;

        /// <summary>
        /// Characters that end a word. Deliberately ASCII-only: CJK text is not
        /// space-separated, so counting every ideograph as its own word would make a
        /// term occurring twice inside one CJK title register as two word-prefix hits,
        /// and the ranking between that document and its neighbours would blur.
        /// </summary>
        private static readonly char[] WordSeparators = { ' ', '\t', '.', '/', '\\', '_', '-', ':', '|' };

        /// <summary>Best match of one token against one field, with enough context to highlight.</summary>
        private readonly record struct FieldMatch(
            double Quality, double Weight, int Field, int Start, int Length, bool Fuzzy);

        public static IReadOnlyList<SearchResult> Search(IReadOnlyList<SearchDoc> docs, string query)
            => Search(docs, query, int.MaxValue);

        public static IReadOnlyList<SearchResult> Search(
            IReadOnlyList<SearchDoc> docs, string query, int limit)
        {
            ArgumentNullException.ThrowIfNull(docs);

            var results = new List<SearchResult>();
            if (limit <= 0) return results;

            var tokens = Tokenize(query);

            // No query: everything matches, in input order, with nothing highlighted.
            if (tokens.Count == 0)
            {
                foreach (var doc in docs)
                    results.Add(new SearchResult(
                        new SearchHit(doc, 0, Array.Empty<string>()),
                        Array.Empty<Range>(),
                        Array.Empty<Range>(),
                        false));

                if (results.Count > limit) results.RemoveRange(limit, results.Count - limit);
                return results;
            }

            foreach (var doc in docs)
            {
                if (!TryScore(doc, tokens, out var score, out var titleRanges,
                        out var dirRanges, out var anyFuzzy))
                    continue;

                results.Add(new SearchResult(
                    new SearchHit(doc, score, tokens),
                    titleRanges,
                    dirRanges,
                    anyFuzzy));
            }

            results.Sort(static (a, b) =>
            {
                var byScore = b.Hit.Score.CompareTo(a.Hit.Score);
                if (byScore != 0) return byScore;
                var byMessages = b.Hit.Doc.Messages.CompareTo(a.Hit.Doc.Messages);
                if (byMessages != 0) return byMessages;
                return string.CompareOrdinal(a.Hit.Doc.Id, b.Hit.Doc.Id);
            });

            if (results.Count > limit) results.RemoveRange(limit, results.Count - limit);
            return results;
        }

        /// <summary>Match count without materialising result objects.</summary>
        public static int Count(IReadOnlyList<SearchDoc> docs, string query)
        {
            ArgumentNullException.ThrowIfNull(docs);

            var tokens = Tokenize(query);
            if (tokens.Count == 0) return docs.Count;

            var count = 0;
            foreach (var doc in docs)
                if (TryScore(doc, tokens, out _, out _, out _, out _)) count++;

            return count;
        }

        private static List<string> Tokenize(string? query)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return tokens;

            foreach (var part in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (part.Length > 0) tokens.Add(part);

            return tokens;
        }

        private static bool TryScore(
            SearchDoc doc, List<string> tokens,
            out double score, out List<Range> titleRanges,
            out List<Range> dirRanges, out bool anyFuzzy)
        {
            score = 0;
            titleRanges = new List<Range>();
            dirRanges = new List<Range>();
            anyFuzzy = false;

            foreach (var token in tokens)
            {
                var match = BestMatch(doc, token);
                if (match.Quality == 0) return false;   // AND semantics

                score += match.Quality * match.Weight;

                if (match.Fuzzy)
                {
                    // A fuzzy hit has no literal span to highlight.
                    anyFuzzy = true;
                }
                else if (match.Length > 0)
                {
                    if (match.Field == FieldTitle) titleRanges.Add(new Range(match.Start, match.Length));
                    else if (match.Field == FieldDirectory) dirRanges.Add(new Range(match.Start, match.Length));
                }
            }

            // Precise multi-token queries should outrank a single lucky token.
            score += (tokens.Count - 1) * MultiTokenBonus;
            return true;
        }

        /// <summary>
        /// Best match of one token across all four fields.
        /// </summary>
        /// <remarks>
        /// Exactly one field wins, so highlight ranges are only ever contributed by that
        /// field. Picking per-field winners independently would let a weaker match in the
        /// folder column emit a stray highlight that contradicts the displayed score.
        ///
        /// The winner is decided on the WEIGHTED score, not on raw match quality. Comparing
        /// quality first looks right and is wrong: a word-prefix hit in the directory
        /// (600 x 6 = 3600) then beats a substring hit in the title (400 x 10 = 4000), so a
        /// session whose title literally contains the query ranks below one that only
        /// matched on its folder name. Ties break on quality then field weight, so the
        /// choice stays deterministic.
        /// </remarks>
        private static FieldMatch BestMatch(SearchDoc doc, string token)
        {
            var best = new FieldMatch(0, 0, FieldNone, 0, 0, false);

            void Consider(string? value, double weight, int field)
            {
                if (string.IsNullOrEmpty(value)) return;

                var quality = Quality(value, token, out var start, out var length);
                if (quality == 0) return;

                // Weighted score decides, not raw quality -- see the remark above.
                var candidate = quality * weight;
                var incumbent = best.Quality * best.Weight;

                var better = candidate > incumbent ||
                             (candidate == incumbent &&
                              (quality > best.Quality || weight > best.Weight));
                if (!better) return;

                best = new FieldMatch(quality, weight, field, start, length, quality == ScoreFuzzy);
            }

            Consider(doc.Title, WeightTitle, FieldTitle);
            Consider(doc.Id, WeightId, FieldId);
            Consider(doc.Directory, WeightDirectory, FieldDirectory);
            Consider(doc.Agent, WeightAgent, FieldAgent);

            return best;
        }

        /// <summary>Rank one token against one field's value.</summary>
        private static double Quality(string field, string token, out int matchStart, out int matchLength)
        {
            matchStart = 0;
            matchLength = 0;

            if (field.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                matchStart = 0;
                matchLength = field.Length;
                return ScoreExact;
            }

            var index = field.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                matchStart = index;
                matchLength = token.Length;

                // "d" should rank "dns resolve" above "bandwidth".
                return WordStartsAt(field, index) ? ScoreWordPrefix : ScoreSubstring;
            }

            return IsSubsequence(field, token) ? ScoreFuzzy : 0;
        }

        private static bool WordStartsAt(string field, int index)
            => index == 0 || Array.IndexOf(WordSeparators, field[index - 1]) >= 0;

        /// <summary>
        /// Case-insensitive subsequence test, walking Unicode scalar values.
        /// </summary>
        /// <remarks>
        /// Rune-based rather than char-based so a CJK query matches a CJK title and a
        /// surrogate pair is never consumed half a character at a time.
        /// <para>
        /// Note the negated <c>!needle.MoveNext()</c>: that call reports whether the needle
        /// has <em>another</em> rune, so false is what means "fully consumed". Returning
        /// true on the positive reading matches on the first character alone, which makes
        /// every query whose initial letter occurs anywhere in the field look like a hit.
        /// </para>
        /// </remarks>
        private static bool IsSubsequence(string field, string token)
        {
            var haystack = field.EnumerateRunes().GetEnumerator();
            var needle = token.EnumerateRunes().GetEnumerator();

            if (!needle.MoveNext()) return true;   // empty token matches anything

            while (haystack.MoveNext())
            {
                if (Rune.ToUpperInvariant(haystack.Current) != Rune.ToUpperInvariant(needle.Current))
                    continue;

                if (!needle.MoveNext()) return true;   // whole needle consumed
            }

            return false;
        }

        /// <summary>Merge overlapping and adjacent ranges so highlighting has no seams.</summary>
        public static IReadOnlyList<Range> Merge(IEnumerable<Range> ranges)
        {
            var ordered = new List<Range>(ranges);
            if (ordered.Count == 0) return ordered;

            ordered.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<Range> { ordered[0] };
            foreach (var range in ordered.Skip(1))
            {
                var last = merged[^1];
                if (range.Start <= last.Start + last.Length)
                {
                    var end = Math.Max(last.Start + last.Length, range.Start + range.Length);
                    merged[^1] = new Range(last.Start, end - last.Start);
                }
                else
                {
                    merged.Add(range);
                }
            }

            return merged;
        }

        internal static string DescribeScore(double score)
            => score.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
