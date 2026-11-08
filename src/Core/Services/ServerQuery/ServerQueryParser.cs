using System.Text;

namespace TeamSpeakManager.Core.Services.ServerQuery;

/// <summary>
/// One ServerQuery reply block: zero or more data records followed by an error line.
/// </summary>
public sealed record QueryReply(bool IsOk, int ErrorId, string ErrorMessage, IReadOnlyList<IReadOnlyDictionary<string, string>> Records)
{
    public static QueryReply Failed(int id, string message) => new(false, id, message, Array.Empty<IReadOnlyDictionary<string, string>>());
}

/// <summary>
/// Parses the documented TeamSpeak 3 ServerQuery text protocol.
/// A reply is a sequence of lines; the last line is <c>error id=N msg=...</c>.
/// Records are pipe-separated newline, keys/values separated by spaces, '=' key/value.
/// Special chars are backslash-escaped (\s space, \p pipe, \/ slash, \\ backslash, \n newline).
/// </summary>
public static class ServerQueryParser
{
    public static QueryReply ParseSingle(string raw)
    {
        var replies = ParseMany(raw);
        return replies.Count > 0 ? replies[^1] : QueryReply.Failed(-1, "empty response");
    }

    public static IReadOnlyList<QueryReply> ParseMany(string raw)
    {
        var replies = new List<QueryReply>();
        if (string.IsNullOrEmpty(raw)) return replies;

        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var dataLines = new List<string>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("error ", StringComparison.Ordinal))
            {
                replies.Add(BuildReply(dataLines, line));
                dataLines.Clear();
            }
            else if (line.Length > 0)
            {
                dataLines.Add(line);
            }
            i++;
        }

        // Trailing data with no error line means the server closed early. Surface it as a fault.
        if (dataLines.Count > 0)
            replies.Add(QueryReply.Failed(-1, "incomplete response (no error line)"));

        return replies;
    }

    private static QueryReply BuildReply(List<string> dataLines, string errorLine)
    {
        var fields = SplitFields(errorLine);
        var id = fields.TryGetValue("id", out var rawId) && int.TryParse(rawId, out var parsed) ? parsed : -1;
        var msg = fields.TryGetValue("msg", out var m) ? m : "";

        var records = new List<IReadOnlyDictionary<string, string>>();
        foreach (var line in dataLines)
        {
            // Records in one line are separated by '|' only after unescaping; split before unescape
            // so a literal \p in a value cannot be mistaken for a separator.
            foreach (var rec in line.Split('|'))
            {
                if (rec.Length == 0) continue;
                var dict = SplitFields(rec);
                if (dict.Count > 0) records.Add(dict);
            }
        }

        return new QueryReply(id == 0, id, msg, records);
    }

    private static Dictionary<string, string> SplitFields(string line)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var token = new StringBuilder();
        var escaped = false;
        var tokens = new List<string>();

        foreach (var ch in line)
        {
            if (escaped)
            {
                token.Append('\\').Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '\\') { escaped = true; continue; }
            if (ch == ' ') { tokens.Add(token.ToString()); token.Clear(); continue; }
            token.Append(ch);
        }
        tokens.Add(token.ToString());
        foreach (var t in tokens)
        {
            if (t.Length == 0) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var key = t[..eq];
            var value = Unescape(t[(eq + 1)..]);
            dict[key] = value;
        }
        return dict;
    }

    /// <summary>Unescape a ServerQuery value per the documented escape table.</summary>
    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var n = value[++i];
            sb.Append(n switch
            {
                's' => ' ',
                'p' => '|',
                '/' => '/',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                _ => n,
            });
        }
        return sb.ToString();
    }

    /// <summary>Escape a value for safe inclusion in a command (prevents argument injection).</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '/' => "\\/",
                ' ' => "\\s",
                '|' => "\\p",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }
}
