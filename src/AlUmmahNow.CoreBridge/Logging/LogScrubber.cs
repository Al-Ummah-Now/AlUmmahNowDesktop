using System.Text.RegularExpressions;

namespace AlUmmahNow.CoreBridge.Logging
{
    /// <summary>Removes secrets and personal data from log text.</summary>
    public static class LogScrubber
    {
        private const string Redacted = "[redacted]";

        private static readonly Regex Bearer = new Regex(@"(?i)\b(bearer)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled);
        private static readonly Regex Jwt = new Regex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(\.[A-Za-z0-9_-]*)?", RegexOptions.Compiled);
        private static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

        // key=value in query strings, forms and JSON-ish text.
        private static readonly Regex SecretParam = new Regex(
            @"(?i)\b(access_token|refresh_token|id_token|token|code|code_verifier|state|password|secret|signature|authorizationsignature|q)(""?\s*[:=]\s*""?)([^&\s"",;]+)",
            RegexOptions.Compiled);

        public static string Scrub(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var s = Bearer.Replace(text, "$1 " + Redacted);
            s = Jwt.Replace(s, Redacted);
            s = SecretParam.Replace(s, "$1$2" + Redacted);
            s = Email.Replace(s, "[email]");
            return s;
        }
    }
}
