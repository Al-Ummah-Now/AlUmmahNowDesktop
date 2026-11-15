using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AlUmmahNow.CoreBridge.Studio
{
    public enum LowerThirdStyle { Bar, Box, Minimal }

    public enum LowerThirdPosition { Start, Center, End }

    public enum WatermarkCorner { None, TopStart, TopEnd, BottomStart, BottomEnd }

    /// <summary>The account's brand kit (<c>GET|PUT /api/studio/brand-kit</c>), shared by its channels and their teams.</summary>
    public sealed class BrandKit : IEquatable<BrandKit>
    {
        /// <summary>The server's defaults (Fanous night teal, lantern and parchment).</summary>
        public const string DefaultPrimary = "#ffa037";
        public const string DefaultSecondary = "#093041";
        public const string DefaultText = "#f7f1e8";
        public const string DefaultBackground = "#093041";
        public const int MaxNameChars = 80;

        public string? NameEn { get; set; }
        public string? NameAr { get; set; }
        public string Primary { get; set; } = DefaultPrimary;
        public string Secondary { get; set; } = DefaultSecondary;
        public string TextColor { get; set; } = DefaultText;
        public string Background { get; set; } = DefaultBackground;

        /// <summary>Asset ids from the library: an image for the logo, videos for the intro and outro.</summary>
        public string? Logo { get; set; }
        public string? Intro { get; set; }
        public string? Outro { get; set; }
        public LowerThirdStyle LowerThirdStyle { get; set; }
        public LowerThirdPosition LowerThirdPosition { get; set; }
        public WatermarkCorner Watermark { get; set; }

        public BrandKit Clone() => (BrandKit)MemberwiseClone();

        /// <summary>The name for a language ("ar" or "en"), falling back to the other one.</summary>
        public string? DisplayName(string language)
        {
            var preferred = language == "ar" ? NameAr : NameEn;
            var other = language == "ar" ? NameEn : NameAr;
            return new[] { preferred, other }.Select(x => x?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        }

        /// <summary><c>#rrggbb</c>, as the server validates it.</summary>
        public static bool IsValidColor(string? value) =>
            value != null && value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

        /// <summary>The first field the server would refuse, or null.</summary>
        public string? Invalid()
        {
            if ((NameEn ?? "").Trim().Length > MaxNameChars) return "name.en";
            if ((NameAr ?? "").Trim().Length > MaxNameChars) return "name.ar";
            if (!IsValidColor(Primary)) return "colors.primary";
            if (!IsValidColor(Secondary)) return "colors.secondary";
            if (!IsValidColor(TextColor)) return "colors.text";
            if (!IsValidColor(Background)) return "colors.background";
            return null;
        }

        internal static string Style(LowerThirdStyle s) => s switch { LowerThirdStyle.Box => "box", LowerThirdStyle.Minimal => "minimal", _ => "bar" };

        internal static string Position(LowerThirdPosition p) => p switch { LowerThirdPosition.Center => "center", LowerThirdPosition.End => "end", _ => "start" };

        public static string Corner(WatermarkCorner w) => w switch
        {
            WatermarkCorner.TopStart => "top-start",
            WatermarkCorner.TopEnd => "top-end",
            WatermarkCorner.BottomStart => "bottom-start",
            WatermarkCorner.BottomEnd => "bottom-end",
            _ => "none",
        };

        public static BrandKit Read(JsonElement k)
        {
            var name = k.TryGetProperty("name", out var n) ? n : default;
            var colors = k.TryGetProperty("colors", out var c) ? c : default;
            var lt = k.TryGetProperty("lowerThird", out var l) ? l : default;
            return new BrandKit
            {
                NameEn = J.Str(name, "en"),
                NameAr = J.Str(name, "ar"),
                Primary = J.Str(colors, "primary") ?? DefaultPrimary,
                Secondary = J.Str(colors, "secondary") ?? DefaultSecondary,
                TextColor = J.Str(colors, "text") ?? DefaultText,
                Background = J.Str(colors, "background") ?? DefaultBackground,
                Logo = J.Str(k, "logo"),
                Intro = J.Str(k, "intro"),
                Outro = J.Str(k, "outro"),
                LowerThirdStyle = J.Str(lt, "style") switch { "box" => LowerThirdStyle.Box, "minimal" => LowerThirdStyle.Minimal, _ => LowerThirdStyle.Bar },
                LowerThirdPosition = J.Str(lt, "position") switch { "center" => LowerThirdPosition.Center, "end" => LowerThirdPosition.End, _ => LowerThirdPosition.Start },
                Watermark = J.Str(k, "watermark") switch
                {
                    "top-start" => WatermarkCorner.TopStart,
                    "top-end" => WatermarkCorner.TopEnd,
                    "bottom-start" => WatermarkCorner.BottomStart,
                    "bottom-end" => WatermarkCorner.BottomEnd,
                    _ => WatermarkCorner.None,
                },
            };
        }

        /// <summary>The PUT body.</summary>
        public string ToJson() => CaptionJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("name");
            if (!string.IsNullOrWhiteSpace(NameEn)) w.WriteString("en", NameEn!.Trim());
            if (!string.IsNullOrWhiteSpace(NameAr)) w.WriteString("ar", NameAr!.Trim());
            w.WriteEndObject();
            w.WriteStartObject("colors");
            w.WriteString("primary", Primary);
            w.WriteString("secondary", Secondary);
            w.WriteString("text", TextColor);
            w.WriteString("background", Background);
            w.WriteEndObject();
            if (!string.IsNullOrEmpty(Logo)) w.WriteString("logo", Logo);
            if (!string.IsNullOrEmpty(Intro)) w.WriteString("intro", Intro);
            if (!string.IsNullOrEmpty(Outro)) w.WriteString("outro", Outro);
            w.WriteStartObject("lowerThird");
            w.WriteString("style", Style(LowerThirdStyle));
            w.WriteString("position", Position(LowerThirdPosition));
            w.WriteEndObject();
            w.WriteString("watermark", Corner(Watermark));
            w.WriteEndObject();
        });

        public bool Equals(BrandKit? o) => o != null && ToJson() == o.ToJson();

        public override bool Equals(object? obj) => Equals(obj as BrandKit);

        public override int GetHashCode() => ToJson().GetHashCode();
    }

    /// <summary>The kit, whether it was ever saved, and whether this account may change it (team members read the owner's kit).</summary>
    public sealed class BrandKitState
    {
        public BrandKitState(BrandKit kit, bool saved, bool canEdit)
        {
            Kit = kit;
            Saved = saved;
            CanEdit = canEdit;
        }

        public BrandKit Kit { get; }
        public bool Saved { get; }
        public bool CanEdit { get; }
    }

    public enum StudioAssetKind { Image, Video, Audio }

    /// <summary>A file in the shared asset library.</summary>
    public sealed class StudioAsset
    {
        public string Id { get; set; } = "";
        public StudioAssetKind Kind { get; set; }
        public string Name { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Bytes { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }

        public static StudioAsset Read(JsonElement a) => new StudioAsset
        {
            Id = J.Str(a, "id") ?? "",
            Kind = J.Str(a, "kind") switch { "video" => StudioAssetKind.Video, "audio" => StudioAssetKind.Audio, _ => StudioAssetKind.Image },
            Name = J.Str(a, "name") ?? "",
            ContentType = J.Str(a, "contentType") ?? "",
            Bytes = J.Long(a, "bytes"),
            CreatedAt = J.Date(a, "createdAt"),
        };
    }

    public sealed class StudioAssetLibrary
    {
        public const int MaxNameChars = 80;

        public IReadOnlyList<StudioAsset> Assets { get; set; } = Array.Empty<StudioAsset>();
        public long Bytes { get; set; }
        public int MaxAssets { get; set; } = 50;
        public long MaxAssetBytes { get; set; } = 95L * 1024 * 1024;
        public long MaxTotalBytes { get; set; } = 1024L * 1024 * 1024;
        public bool CanEdit { get; set; } = true;

        /// <summary>Why a new file can't be added (<c>empty</c>, <c>too_large</c>, <c>library_full</c>), checked before sending the bytes.</summary>
        public string? Refusal(long size)
        {
            if (size <= 0) return "empty";
            if (size > MaxAssetBytes) return "too_large";
            if (Assets.Count >= MaxAssets || Bytes + size > MaxTotalBytes) return "library_full";
            return null;
        }

        public IEnumerable<StudioAsset> OfKind(StudioAssetKind kind) => Assets.Where(a => a.Kind == kind);

        public static StudioAssetLibrary Read(JsonElement d)
        {
            var assets = J.Array(d, "assets").Select(StudioAsset.Read).Where(a => a.Id.Length > 0).ToList();
            var limits = d.TryGetProperty("limits", out var l) ? l : default;
            return new StudioAssetLibrary
            {
                Assets = assets,
                Bytes = J.NumOrNull(d, "bytes") is double b ? (long)b : assets.Sum(a => a.Bytes),
                MaxAssets = (int)(J.NumOrNull(limits, "maxAssets") ?? 50),
                MaxAssetBytes = (long)(J.NumOrNull(limits, "maxAssetBytes") ?? 95L * 1024 * 1024),
                MaxTotalBytes = (long)(J.NumOrNull(limits, "maxTotalBytes") ?? 1024L * 1024 * 1024),
                CanEdit = d.TryGetProperty("canEdit", out var ce) ? ce.ValueKind == JsonValueKind.True : true,
            };
        }
    }
}
