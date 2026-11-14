using System.Runtime.InteropServices;
using AlUmmahNow.Platform.Windows.WebAuthn;
using Xunit;

namespace AlUmmahNow.Platform.Windows.Tests;

/// <summary>
/// Struct layouts against the C header: layout/webauthn_layout.c prints sizeof/offsetof for every
/// field the app marshals. The committed file comes from microsoft/webauthn's webauthn.h (API 9);
/// Windows CI also compiles it against the SDK's header and sets AUN_WEBAUTHN_LAYOUT.
/// </summary>
public class WebAuthnLayoutTests
{
    private static readonly Type[] Structs = typeof(WEBAUTHN_ASSERTION).Assembly.GetTypes()
        .Where(t => t.IsValueType && t.Name.StartsWith("WEBAUTHN_", StringComparison.Ordinal)).ToArray();

    public static IEnumerable<object[]> Files()
    {
        yield return new object[] { Path.Combine(AppContext.BaseDirectory, "layout", "webauthn_layout.expected.txt") };
        if (Environment.GetEnvironmentVariable("AUN_WEBAUTHN_LAYOUT") is { Length: > 0 } sdk) yield return new object[] { sdk };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Layout_matches_the_c_header(string file)
    {
        Assert.Equal(8, IntPtr.Size); // x64 and arm64: the only Windows builds.
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0 && !l.StartsWith("API ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lines);
        var checkedFields = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            var type = Structs.Single(t => t.Name == parts[0]);
            var expected = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var actual = parts[1] == "*" ? Marshal.SizeOf(type) : (int)Marshal.OffsetOf(type, parts[1]);
            Assert.True(expected == actual, $"{parts[0]}.{parts[1]}: C {expected}, .NET {actual}");
            checkedFields++;
        }

        Assert.True(checkedFields > 100, "only " + checkedFields + " layout lines");
    }

    [Fact]
    public void Every_marshalled_field_is_in_the_layout_program()
    {
        var c = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "layout", "webauthn_layout.expected.txt"));
        foreach (var type in Structs)
        {
            foreach (var field in type.GetFields())
            {
                Assert.Contains(type.Name + " " + field.Name + " ", c);
            }
        }
    }
}
