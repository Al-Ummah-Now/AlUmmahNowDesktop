using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AlUmmahNow.App.Theme
{
    /// <summary>
    /// The Fawanees tokens from the shared native theme registry (AlUmmahNow packages/fanous-tokens,
    /// vendored by scripts/sync-fanous.sh): WinUI dictionaries and GTK stylesheets, for Studio and the viewer.
    /// </summary>
    public static class Fanous
    {
        /// <summary>"Fanous.Colors.xaml", "Fanous.Tokens.xaml", "style.css" (light), "style-dark.css".</summary>
        public static string File(string name)
        {
            var stream = typeof(Fanous).Assembly.GetManifestResourceStream("fanous/" + name) ?? throw new InvalidOperationException("missing resource fanous/" + name);
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        /// <summary>
        /// The Color entries of a generated WinUI dictionary (Fanous.Colors.xaml), as ARGB per theme key
        /// ("Default" = dark, "Light"). Read as XML, so no XAML runtime is involved.
        /// </summary>
        public static Dictionary<string, Dictionary<string, uint>> ReadXamlColors(string xaml)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var result = new Dictionary<string, Dictionary<string, uint>>();
            foreach (var theme in XDocument.Parse(xaml).Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(x + "Key") != null))
            {
                var colors = new Dictionary<string, uint>();
                foreach (var c in theme.Elements().Where(e => e.Name.LocalName == "Color"))
                {
                    var hex = c.Value.Trim().TrimStart('#');
                    colors[(string)c.Attribute(x + "Key")!] = Convert.ToUInt32(hex.Length == 6 ? "FF" + hex : hex, 16);
                }

                result[(string)theme.Attribute(x + "Key")!] = colors;
            }

            return result;
        }
    }
}
