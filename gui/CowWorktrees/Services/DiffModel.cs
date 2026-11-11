using System.Text;

namespace CowWorktrees.Services;

/// <summary>What happened to a file between the two sides of a comparison.</summary>
public enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    /// <summary>A new file Git does not track yet.</summary>
    Untracked,
    TypeChanged,
    Conflict,
    /// <summary>Only looks changed to Git: identical to the commit (see GitService.MarkPhantomsAsync).</summary>
    Phantom,
}

public enum DiffLineKind { Context, Added, Removed }

public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }
    /// <summary>Line number on the old side; 0 for an added line.</summary>
    public int OldNo { get; init; }
    /// <summary>Line number on the new side; 0 for a removed line.</summary>
    public int NewNo { get; init; }
    /// <summary>The line without its +/- marker, tabs widened to spaces.</summary>
    public string Text { get; init; } = "";
    /// <summary>The part of the line that differs from its partner line (removed against added); Length 0 = none.</summary>
    public int HighlightStart { get; set; }
    public int HighlightLength { get; set; }
    /// <summary>The file's last line, and it has no line break after it.</summary>
    public bool NoNewline { get; set; }
}

public sealed class DiffHunk
{
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }
    /// <summary>What Git prints after the second @@: usually the function the lines are in.</summary>
    public string Context { get; init; } = "";
    public List<DiffLine> Lines { get; } = new();
}

/// <summary>One file of a comparison: what happened to it, how much, and (when loaded) the changed lines.</summary>
public sealed class ChangedFile
{
    /// <summary>Path in the worktree, with forward slashes.</summary>
    public required string Path { get; init; }
    /// <summary>Where it was before a rename.</summary>
    public string? OldPath { get; set; }
    public ChangeKind Kind { get; set; }
    public int Added { get; set; }
    public int Deleted { get; set; }
    public bool Binary { get; set; }
    /// <summary>Part of it is already staged for the next commit (uncommitted changes only).</summary>
    public bool Staged { get; set; }
    public List<DiffHunk> Hunks { get; } = new();
    /// <summary>Lines of the file's diff that were not kept because it is very long.</summary>
    public int LinesLeftOut { get; set; }
    /// <summary>Why there are no lines to show, in a sentence; null when there are (or nothing needs saying).</summary>
    public string? Note { get; set; }
    public int LineCount => Hunks.Sum(h => h.Lines.Count);
}

/// <summary>
/// Reads `git diff` output (unified format, a/ and b/ prefixes) one line at a time. Hunk bodies are counted
/// from their @@ header, so a removed line that reads "-- a/x" is never taken for a file header.
/// </summary>
public sealed class DiffParser
{
    public sealed class ParsedFile
    {
        public string? OldPath;
        public string? NewPath;
        public string? RenameFrom;
        public string? RenameTo;
        public string Header = "";
        public bool Binary;
        public bool IsNew;
        public bool IsDeleted;
        public int LinesLeftOut;
        public List<DiffHunk> Hunks { get; } = new();

        /// <summary>The path the file has now (or had, when it was deleted).</summary>
        public string Path => RenameTo ?? NewPath ?? OldPath ?? PathFromHeader(Header) ?? "";
    }

    private readonly int _maxLinesPerFile;
    private readonly List<ParsedFile> _files = new();
    private ParsedFile? _file;
    private DiffHunk? _hunk;
    private int _oldLeft, _newLeft, _oldNo, _newNo, _kept;

    public DiffParser(int maxLinesPerFile = 6000) => _maxLinesPerFile = maxLinesPerFile;

    public IReadOnlyList<ParsedFile> Files => _files;
    /// <summary>Lines kept so far, over all files.</summary>
    public int TotalLines { get; private set; }

    public static List<ParsedFile> Parse(IEnumerable<string> lines, int maxLinesPerFile = 6000)
    {
        var p = new DiffParser(maxLinesPerFile);
        foreach (var l in lines) p.Feed(l);
        return p.Finish();
    }

    public List<ParsedFile> Finish()
    {
        CloseHunk();
        return _files;
    }

    public void Feed(string line)
    {
        if (_hunk != null && (_oldLeft > 0 || _newLeft > 0))
        {
            FeedBody(line);
            return;
        }
        if (line.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            CloseHunk();
            _file = new ParsedFile { Header = line };
            _files.Add(_file);
            _kept = 0;
            return;
        }
        if (_file == null) return;
        if (line.StartsWith("@@ ", StringComparison.Ordinal))
        {
            CloseHunk();
            StartHunk(line);
            return;
        }
        if (line.StartsWith("\\ ", StringComparison.Ordinal))   // "\ No newline at end of file", about the line before it
        {
            if (_hunk is { Lines.Count: > 0 }) _hunk.Lines[^1].NoNewline = true;
            return;
        }
        if (_hunk != null) return;   // between a finished hunk and the next header: nothing Git prints is of use
        if (line.StartsWith("--- ", StringComparison.Ordinal)) _file.OldPath = HeaderPath(line[4..], "a/");
        else if (line.StartsWith("+++ ", StringComparison.Ordinal)) _file.NewPath = HeaderPath(line[4..], "b/");
        else if (line.StartsWith("rename from ", StringComparison.Ordinal)) _file.RenameFrom = Unquote(line[12..]);
        else if (line.StartsWith("rename to ", StringComparison.Ordinal)) _file.RenameTo = Unquote(line[10..]);
        else if (line.StartsWith("new file mode", StringComparison.Ordinal)) _file.IsNew = true;
        else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) _file.IsDeleted = true;
        else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) _file.Binary = true;
    }

    private void StartHunk(string line)
    {
        // @@ -12,7 +12,9 @@ optional context
        int end = line.IndexOf(" @@", 3, StringComparison.Ordinal);
        if (end < 0) return;
        var parts = line[3..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Range(parts[0], '-', out int os, out int oc) || !Range(parts[1], '+', out int ns, out int nc)) return;
        _hunk = new DiffHunk { OldStart = os, OldCount = oc, NewStart = ns, NewCount = nc, Context = line.Length > end + 3 ? line[(end + 3)..].Trim() : "" };
        _file!.Hunks.Add(_hunk);
        _oldLeft = oc; _newLeft = nc; _oldNo = os; _newNo = ns;
    }

    private static bool Range(string s, char sign, out int start, out int count)
    {
        start = 0; count = 1;
        if (s.Length < 2 || s[0] != sign) return false;
        int comma = s.IndexOf(',');
        return comma < 0 ? int.TryParse(s[1..], out start) : int.TryParse(s[1..comma], out start) && int.TryParse(s[(comma + 1)..], out count);
    }

    private void FeedBody(string line)
    {
        char c = line.Length > 0 ? line[0] : ' ';
        string text = line.Length > 1 ? Expand(line[1..]) : "";
        DiffLine? made = null;
        switch (c)
        {
            case '+':
                made = new DiffLine { Kind = DiffLineKind.Added, NewNo = _newNo++, Text = text };
                _newLeft--;
                break;
            case '-':
                made = new DiffLine { Kind = DiffLineKind.Removed, OldNo = _oldNo++, Text = text };
                _oldLeft--;
                break;
            case '\\':
                if (_hunk!.Lines.Count > 0) _hunk.Lines[^1].NoNewline = true;
                return;
            default:   // a context line; an empty line can arrive with its single space trimmed
                made = new DiffLine { Kind = DiffLineKind.Context, OldNo = _oldNo++, NewNo = _newNo++, Text = c == ' ' ? text : Expand(line) };
                _oldLeft--; _newLeft--;
                break;
        }
        if (_kept < _maxLinesPerFile)
        {
            _hunk!.Lines.Add(made);
            _kept++;
            TotalLines++;
        }
        else _file!.LinesLeftOut++;
    }

    private void CloseHunk()
    {
        if (_hunk != null) IntraLine.Mark(_hunk);
        _hunk = null;
        _oldLeft = _newLeft = 0;
    }

    private static string? HeaderPath(string s, string prefix)
    {
        s = s.TrimEnd('\t');   // Git ends the name with a tab when it has spaces
        if (s == "/dev/null") return null;
        s = Unquote(s);
        return s.StartsWith(prefix, StringComparison.Ordinal) ? s[2..] : s;
    }

    /// <summary>"diff --git a/P b/P" for a file that was not renamed: both halves name the same path.</summary>
    private static string? PathFromHeader(string header)
    {
        const string lead = "diff --git ";
        if (!header.StartsWith(lead, StringComparison.Ordinal)) return null;
        string rest = header[lead.Length..];
        if (rest.Length % 2 == 0 || rest.Length < 7) return null;
        int half = (rest.Length - 1) / 2;
        string a = rest[..half], b = rest[(half + 1)..];
        if (rest[half] != ' ' || !a.StartsWith("a/", StringComparison.Ordinal) || !b.StartsWith("b/", StringComparison.Ordinal)) return null;
        return a[2..] == b[2..] ? a[2..] : null;
    }

    /// <summary>Git puts a name in double quotes, C-style, when it holds a quote, a backslash or a control character.</summary>
    public static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>(s.Length);
        for (int i = 1; i < s.Length - 1; i++)
        {
            char ch = s[i];
            if (ch != '\\' || i + 1 >= s.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(ch.ToString()));
                continue;
            }
            char n = s[++i];
            if (n >= '0' && n <= '7' && i + 2 < s.Length - 1)
            {
                bytes.Add(Convert.ToByte(s.Substring(i, 3), 8));
                i += 2;
            }
            else bytes.Add((byte)(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n }));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Tabs widened to the next multiple of four columns, so every line lines up in a text block.</summary>
    public static string Expand(string s)
    {
        if (s.IndexOf('\t') < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        foreach (char ch in s)
        {
            if (ch == '\t') sb.Append(' ', 4 - sb.Length % 4);
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Marks what changed inside a line: where a run of removed lines is followed by as many added lines, each
/// pair is compared and the part between their common start and common end is the highlight.
/// </summary>
public static class IntraLine
{
    public static void Mark(DiffHunk hunk)
    {
        var lines = hunk.Lines;
        int i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind != DiffLineKind.Removed) { i++; continue; }
            int r0 = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) i++;
            int a0 = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) i++;
            int removed = a0 - r0, added = i - a0;
            if (added == 0) continue;
            if (removed == added)
            {
                if (removed <= 300) for (int k = 0; k < removed; k++) Pair(lines[r0 + k], lines[a0 + k], 25);
                continue;
            }
            // Not line for line: each removed line goes with the added line most like it, in order, when one is alike enough.
            if ((long)removed * added > 900) continue;
            int from = a0;
            for (int r = r0; r < a0 && from < i; r++)
            {
                int best = -1, bestScore = 49;
                for (int a = from; a < i; a++)
                {
                    int score = Score(lines[r].Text, lines[a].Text, out _, out _);
                    if (score > bestScore) { bestScore = score; best = a; }
                }
                if (best < 0) continue;
                Pair(lines[r], lines[best], 25);
                from = best + 1;
            }
        }
    }

    private static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>How alike two lines are, 0 to 100: the share of the longer one (without its indent) that their common start and end cover.</summary>
    private static int Score(string a, string b, out int p, out int s)
    {
        p = s = 0;
        if (a.Length > 2000 || b.Length > 2000) return 0;
        int max = Math.Min(a.Length, b.Length);
        while (p < max && a[p] == b[p]) p++;
        while (s < max - p && a[a.Length - 1 - s] == b[b.Length - 1 - s]) s++;
        // Not the middle of a word: "count" -> "counter" marks the whole word, not "er".
        while (p > 0 && Word(a[p - 1]) && ((p < a.Length && Word(a[p])) || (p < b.Length && Word(b[p])))) p--;
        while (s > 0 && Word(a[a.Length - s]) && ((a.Length - s - 1 >= p && Word(a[a.Length - s - 1])) || (b.Length - s - 1 >= p && Word(b[b.Length - s - 1])))) s--;

        int indent = 0;
        while (indent < p && char.IsWhiteSpace(a[indent])) indent++;
        int common = (p - indent) + s;
        int longest = Math.Max(a.Length, b.Length) - indent;
        return longest <= 0 ? 0 : common * 100 / longest;
    }

    private static void Pair(DiffLine oldLine, DiffLine newLine, int atLeast)
    {
        string a = oldLine.Text, b = newLine.Text;
        if (a == b) return;
        // Two different lines: colouring nearly all of both says nothing.
        if (Score(a, b, out int p, out int s) < atLeast) return;
        int aLen = a.Length - p - s, bLen = b.Length - p - s;
        if (aLen > 0) { oldLine.HighlightStart = p; oldLine.HighlightLength = aLen; }
        if (bLen > 0) { newLine.HighlightStart = p; newLine.HighlightLength = bLen; }
    }
}
