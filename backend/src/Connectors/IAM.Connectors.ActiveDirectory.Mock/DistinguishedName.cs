namespace IAM.Connectors.ActiveDirectory.Mock;

/// <summary>
/// The subset of LDAP distinguished-name syntax (RFC 4514) the mock accepts (DL-081): relative names of type CN, OU or DC,
/// separated by commas; special characters in a value only escaped with a backslash; no hex escapes, no multi-valued
/// names; and every name inside the mock's base DN. Anything else is refused, so an identifier can't smuggle in a filter
/// or a second name. Names compare ignoring case, as in a real directory (DL-083b).
/// </summary>
public static class DistinguishedName
{
    private const string Specials = ",+\"\\<>;=#";

    /// <summary>The relative names of <paramref name="dn"/>, outermost last, or null if it isn't one the mock accepts.</summary>
    public static IReadOnlyList<(string Type, string Value)>? Parse(string? dn)
    {
        if (string.IsNullOrEmpty(dn) || dn.Length > 512)
        {
            return null;
        }

        var parts = new List<(string, string)>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i <= dn.Length; i++)
        {
            if (i == dn.Length || dn[i] == ',')
            {
                if (Rdn(current.ToString()) is not { } rdn)
                {
                    return null;
                }

                parts.Add(rdn);
                current.Clear();
                continue;
            }

            if (dn[i] == '\\')
            {
                if (i + 1 >= dn.Length || !Specials.Contains(dn[i + 1], StringComparison.Ordinal))
                {
                    return null;
                }

                current.Append(dn[i]).Append(dn[i + 1]);
                i++;
                continue;
            }

            current.Append(dn[i]);
        }

        return parts;
    }

    /// <summary>A valid name strictly inside <paramref name="baseDn"/>, compared part by part (values ignoring case, as AD does).</summary>
    public static bool IsValid(string? dn, string baseDn)
    {
        if (Parse(dn) is not { Count: > 0 } parts || Parse(baseDn) is not { } bases || parts.Count <= bases.Count)
        {
            return false;
        }

        var tail = parts.Skip(parts.Count - bases.Count);
        return tail.Zip(bases).All(p => p.First.Type == p.Second.Type && string.Equals(p.First.Value, p.Second.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether two names are the same object's, as AD compares them (ignoring case).</summary>
    public static bool SameName(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The DN one level up: the container an object is in.</summary>
    public static string? Parent(string dn)
    {
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\')
            {
                i++;
            }
            else if (dn[i] == ',')
            {
                return dn[(i + 1)..];
            }
        }

        return null;
    }

    private static (string Type, string Value)? Rdn(string text)
    {
        var equals = text.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return null;
        }

        var type = text[..equals];
        var value = text[(equals + 1)..];
        if (type is not ("CN" or "OU" or "DC") || value.Length == 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return null;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
            {
                i++;
            }
            else if (Specials.Contains(c, StringComparison.Ordinal) || char.IsControl(c))
            {
                return null;
            }
        }

        return (type, value);
    }
}
