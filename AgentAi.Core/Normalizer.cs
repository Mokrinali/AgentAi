using System.Text.RegularExpressions;

namespace AgentAi.Core;

public static class Normalizer
{
    private static readonly Regex Punct = new("[\\p{P}\\p{S}]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled);

    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim().ToLowerInvariant();
        s = Punct.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        return s;
    }

    public static string NormalizePhoneE164(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("995")) return "+" + digits;
        if (phone.StartsWith("+")) return phone;
        return "+" + digits;
    }
}
