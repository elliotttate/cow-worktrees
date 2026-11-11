namespace CowWorktrees.Services;

/// <summary>Which rough rules a file's text follows, chosen by its name.</summary>
public enum SyntaxFamily : byte
{
    None,
    /// <summary>// and /* */ comments: C, C++, C#, Java, JavaScript, TypeScript, Go, Rust, shaders ...</summary>
    CLike,
    /// <summary># comments: Python, PowerShell, shell, Ruby, YAML, TOML, CMake ...</summary>
    Hash,
    /// <summary>-- comments: SQL, Lua.</summary>
    Dash,
    /// <summary>Tags: XML, XAML, HTML, project files.</summary>
    Markup,
    Json,
}

public enum TokenKind : byte { Comment, String, Number, Keyword, Meta }

public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// Finds comments, strings, numbers and keywords in one line of code, for colouring it in the Changes view.
/// It is a tint, not a parser: it looks at one line at a time, so a comment or string that runs over several
/// lines is only recognised where a line shows it. The tokens never overlap and come in order.
/// </summary>
public static class SyntaxTint
{
    private static readonly IReadOnlyList<SyntaxToken> Nothing = Array.Empty<SyntaxToken>();

    private static readonly HashSet<string> CLikeExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hxx", ".inl", ".ipp", ".cs", ".java", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".go", ".rs",
        ".swift", ".kt", ".kts", ".m", ".mm", ".hlsl", ".glsl", ".fx", ".fxh", ".usf", ".ush", ".shader", ".cg", ".vert", ".frag", ".php", ".css", ".scss", ".dart", ".scala", ".groovy", ".gradle", ".proto",
    };
    private static readonly HashSet<string> HashExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".rb", ".pl", ".pm", ".yml", ".yaml", ".toml", ".cmake", ".mk", ".r", ".tcl", ".conf", ".gitignore", ".gitattributes", ".dockerfile", ".tf",
    };
    private static readonly HashSet<string> MarkupExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xaml", ".html", ".htm", ".svg", ".vcxproj", ".csproj", ".props", ".targets", ".filters", ".resx", ".config", ".manifest", ".plist", ".vue", ".iss",
    };

    public static SyntaxFamily FamilyOf(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length == 0)
        {
            string name = Path.GetFileName(path);
            return name is "Makefile" or "Dockerfile" or "CMakeLists" or ".gitignore" or ".gitattributes" ? SyntaxFamily.Hash : SyntaxFamily.None;
        }
        if (CLikeExt.Contains(ext)) return SyntaxFamily.CLike;
        if (HashExt.Contains(ext) || path.EndsWith("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return SyntaxFamily.Hash;
        if (MarkupExt.Contains(ext)) return SyntaxFamily.Markup;
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)) return SyntaxFamily.Json;
        if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lua", StringComparison.OrdinalIgnoreCase)) return SyntaxFamily.Dash;
        return SyntaxFamily.None;
    }

    private static readonly HashSet<string> CLikeWords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue", "return", "goto", "try", "catch", "finally", "throw", "throws",
        "new", "delete", "class", "struct", "enum", "union", "interface", "namespace", "using", "import", "export", "package", "module", "public", "private", "protected", "internal",
        "static", "const", "constexpr", "consteval", "volatile", "mutable", "virtual", "override", "final", "abstract", "sealed", "readonly", "inline", "extern", "explicit", "friend",
        "typedef", "typename", "template", "concept", "requires", "operator", "this", "base", "super", "null", "nullptr", "NULL", "true", "false", "undefined",
        "void", "bool", "char", "wchar_t", "short", "int", "long", "float", "double", "unsigned", "signed", "auto", "var", "let", "function", "func", "fn", "async", "await", "yield",
        "in", "of", "is", "as", "out", "ref", "sizeof", "typeof", "instanceof", "decltype", "noexcept", "string", "object", "byte", "sbyte", "uint", "ulong", "ushort", "decimal", "dynamic",
        "size_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t", "int8_t", "int16_t", "int32_t", "int64_t", "impl", "trait", "pub", "mut", "use", "mod", "crate", "match", "where", "loop",
        "type", "defer", "go", "chan", "map", "range", "select", "extends", "implements", "get", "set", "record", "partial", "lock", "checked", "unsafe", "params", "when", "nameof", "self", "Self",
    };
    private static readonly HashSet<string> HashWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "def", "class", "if", "elif", "else", "elseif", "for", "foreach", "while", "in", "not", "and", "or", "is", "return", "import", "from", "as", "try", "except", "catch", "finally",
        "raise", "throw", "with", "lambda", "yield", "pass", "break", "continue", "None", "True", "False", "self", "function", "param", "switch", "end", "do", "then", "fi", "done", "esac",
        "case", "begin", "process", "global", "nonlocal", "async", "await", "del", "assert", "local", "export", "exit", "until", "unless", "module", "require", "filter", "trap", "using",
    };
    private static readonly HashSet<string> DashWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "insert", "into", "values", "update", "set", "delete", "create", "alter", "drop", "table", "index", "view", "join", "left", "right", "inner", "outer",
        "on", "group", "by", "order", "having", "union", "as", "and", "or", "not", "null", "is", "in", "like", "between", "case", "when", "then", "else", "end", "begin", "commit",
        "function", "local", "if", "elseif", "for", "while", "do", "repeat", "until", "return", "break", "nil", "true", "false",
    };
    private static readonly HashSet<string> JsonWords = new(StringComparer.Ordinal) { "true", "false", "null" };

    private static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static IReadOnlyList<SyntaxToken> Tokens(string s, SyntaxFamily family)
    {
        if (family == SyntaxFamily.None || s.Length == 0 || s.Length > 1500) return Nothing;
        var list = new List<SyntaxToken>();
        int n = s.Length;
        if (family == SyntaxFamily.Markup)
        {
            Markup(s, list);
            return list;
        }

        int i = 0, first = 0;
        while (first < n && s[first] == ' ') first++;
        if (first == n) return Nothing;
        if (family == SyntaxFamily.CLike)
        {
            // Inside a /* */ block the lines usually start with a star.
            if (s[first] == '*' && (first + 1 == n || s[first + 1] == ' ' || s[first + 1] == '/' || s[first + 1] == '*'))
            {
                list.Add(new SyntaxToken(first, n - first, TokenKind.Comment));
                return list;
            }
            if (s[first] == '#')   // #include, #if, #pragma
            {
                int e = first + 1;
                while (e < n && s[e] == ' ') e++;
                while (e < n && char.IsLetter(s[e])) e++;
                list.Add(new SyntaxToken(first, e - first, TokenKind.Meta));
                i = e;
            }
        }
        var words = family switch { SyntaxFamily.CLike => CLikeWords, SyntaxFamily.Hash => HashWords, SyntaxFamily.Dash => DashWords, _ => JsonWords };

        while (i < n && list.Count < 400)
        {
            char c = s[i];
            if (family == SyntaxFamily.CLike && c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                list.Add(new SyntaxToken(i, n - i, TokenKind.Comment));
                break;
            }
            if (family == SyntaxFamily.CLike && c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? n : e + 2;
                list.Add(new SyntaxToken(i, e - i, TokenKind.Comment));
                i = e;
                continue;
            }
            if (family == SyntaxFamily.Hash && c == '#' && (i == 0 || s[i - 1] == ' '))
            {
                list.Add(new SyntaxToken(i, n - i, TokenKind.Comment));
                break;
            }
            if (family == SyntaxFamily.Dash && c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                list.Add(new SyntaxToken(i, n - i, TokenKind.Comment));
                break;
            }
            if (c == '"' || c == '\'' || (c == '`' && family == SyntaxFamily.CLike))
            {
                int e = i + 1;
                while (e < n && s[e] != c)
                {
                    if (s[e] == '\\') e++;
                    e++;
                }
                bool closed = e < n;
                if (!closed && c != '"')
                {
                    i++;   // an apostrophe in a word, a Rust lifetime, a digit separator: not a string
                    continue;
                }
                e = Math.Min(n, e + 1);
                var kind = TokenKind.String;
                if (family == SyntaxFamily.Json && closed)
                {
                    int after = e;
                    while (after < n && s[after] == ' ') after++;
                    if (after < n && s[after] == ':') kind = TokenKind.Meta;   // a property's name
                }
                list.Add(new SyntaxToken(i, e - i, kind));
                i = e;
                continue;
            }
            if (char.IsAsciiDigit(c) && (i == 0 || !Word(s[i - 1])))
            {
                int e = i + 1;
                while (e < n && (char.IsLetterOrDigit(s[e]) || s[e] == '.' || s[e] == '_')) e++;
                list.Add(new SyntaxToken(i, e - i, TokenKind.Number));
                i = e;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int e = i + 1;
                while (e < n && Word(s[e])) e++;
                if (e - i <= 12 && words.Contains(s.Substring(i, e - i))) list.Add(new SyntaxToken(i, e - i, TokenKind.Keyword));
                i = e;
                continue;
            }
            i++;
        }
        return list;
    }

    private static void Markup(string s, List<SyntaxToken> list)
    {
        int i = 0, n = s.Length;
        while (i < n && list.Count < 400)
        {
            char c = s[i];
            if (c == '<' && string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
            {
                int e = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                e = e < 0 ? n : e + 3;
                list.Add(new SyntaxToken(i, e - i, TokenKind.Comment));
                i = e;
            }
            else if (c == '<')
            {
                int j = i + 1;
                if (j < n && (s[j] == '/' || s[j] == '?')) j++;
                int e = j;
                while (e < n && (char.IsLetterOrDigit(s[e]) || s[e] is ':' or '-' or '_' or '.')) e++;
                if (e > j) list.Add(new SyntaxToken(j, e - j, TokenKind.Keyword));
                i = Math.Max(e, i + 1);
            }
            else if (c == '"')
            {
                int e = s.IndexOf('"', i + 1);
                e = e < 0 ? n : e + 1;
                list.Add(new SyntaxToken(i, e - i, TokenKind.String));
                i = e;
            }
            else i++;
        }
    }
}
