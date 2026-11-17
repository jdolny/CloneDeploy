namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Reads and compares the locations of objects within a directory.
/// </summary>
public static class DistinguishedNameUtility
{
    /// <summary>
    /// Removes the first part of an Active Directory path to get its parent.
    /// Returns an empty string if there is no parent.
    /// </summary>
    public static string Parent(string distinguishedName)
    {
        var index = FindFirstUnescapedComma(distinguishedName);

        return index < 0 ? string.Empty : distinguishedName[(index + 1)..].TrimStart();
    }

    /// <summary>
    /// Checks whether a path matches the base path or ends with a comma followed by it,
    /// ignoring capitalization. This is a text comparison, not a full check of the path's structure.
    /// </summary>
    public static bool IsWithin(string distinguishedName, string baseDn)
    {
        return distinguishedName.Equals(baseDn, StringComparison.OrdinalIgnoreCase)
               || distinguishedName.EndsWith("," + baseDn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the first comma that separates parts of a path, skipping characters escaped with a backslash.
    /// Returns -1 if no separating comma is found.
    /// </summary>
    private static int FindFirstUnescapedComma(string value)
    {
        var escaped = false;

        for (var i = 0; i < value.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (value[i] == ',')
                return i;
        }

        return -1;
    }
    
    public static bool Equals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
