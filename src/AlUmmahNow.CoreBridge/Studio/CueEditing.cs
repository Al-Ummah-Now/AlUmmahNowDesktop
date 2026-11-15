using System;
using System.Collections.Generic;
using System.Linq;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>
    /// Cue edits that keep a draft valid for the server's <c>validateCaptionCues</c> (ids unique, end after start,
    /// at most 10 minutes per cue). Quran segments are never changed. Every edit returns a new list.
    /// </summary>
    public static class CueEditing
    {
        /// <summary>The smallest cue the editor makes, and the step of the nudge buttons.</summary>
        public const int MinimumMs = 100;
        public const int NudgeMs = 100;
        public const int MaximumMs = 10 * 60 * 1000;
        public const int MaxSegments = 24;

        /// <summary>A new id no cue uses (<c>[A-Za-z0-9_-]{1,40}</c>).</summary>
        public static string FreshId(IReadOnlyList<CaptionCue> cues)
        {
            var used = new HashSet<string>(cues.Select(c => c.Id));
            var n = cues.Count + 1;
            while (used.Contains("e" + n)) n++;
            return "e" + n;
        }

        /// <summary>Replaces the text of a speech segment. An emptied cue keeps an empty speech segment until saved.</summary>
        public static IReadOnlyList<CaptionCue> SetText(IReadOnlyList<CaptionCue> cues, int index, int segment, string text)
        {
            if (!Has(cues, index) || segment < 0 || segment >= cues[index].Segments.Count || cues[index].Segments[segment].Speech == null) return cues;
            var segs = cues[index].Segments.ToList();
            segs[segment] = CaptionSegment.Text(text);
            return Replace(cues, index, cues[index].With(segments: segs));
        }

        /// <summary>
        /// Splits a cue at a character offset in one of its speech segments; time is shared by the characters on each
        /// side. Null when either side would be shorter than <see cref="MinimumMs"/> or empty.
        /// </summary>
        public static IReadOnlyList<CaptionCue>? Split(IReadOnlyList<CaptionCue> cues, int index, int segment, int offset)
        {
            if (!Has(cues, index)) return null;
            var cue = cues[index];
            if (segment < 0 || segment >= cue.Segments.Count) return null;
            var text = cue.Segments[segment].Speech;
            if (text == null || offset < 0 || offset > text.Length) return null;
            var head = text.Substring(0, offset).Trim();
            var tail = text.Substring(offset).Trim();
            var first = cue.Segments.Take(segment).ToList();
            if (head.Length > 0) first.Add(CaptionSegment.Text(head));
            var second = new List<CaptionSegment>();
            if (tail.Length > 0) second.Add(CaptionSegment.Text(tail));
            second.AddRange(cue.Segments.Skip(segment + 1));
            if (first.Count == 0 || second.Count == 0) return null;
            var share = (double)Weight(first) / (Weight(first) + Weight(second));
            var at = cue.StartMs + (int)Math.Round((cue.EndMs - cue.StartMs) * share, MidpointRounding.AwayFromZero);
            if (at - cue.StartMs < MinimumMs || cue.EndMs - at < MinimumMs) return null;
            var list = cues.ToList();
            list[index] = cue.With(endMs: at, segments: first);
            list.Insert(index + 1, new CaptionCue(FreshId(cues), at, cue.EndMs, second));
            return list;
        }

        /// <summary>Joins a cue with the next one; neighbouring speech is joined with a space. Null past 10 minutes or 24 segments.</summary>
        public static IReadOnlyList<CaptionCue>? MergeWithNext(IReadOnlyList<CaptionCue> cues, int index)
        {
            if (!Has(cues, index) || !Has(cues, index + 1)) return null;
            var next = cues[index + 1];
            var segs = cues[index].Segments.ToList();
            foreach (var s in next.Segments)
            {
                if (s.Speech != null && segs.Count > 0 && segs[segs.Count - 1].Speech is string last)
                {
                    segs[segs.Count - 1] = CaptionSegment.Text(string.Join(" ", new[] { last, s.Speech }.Where(x => x.Length > 0)));
                }
                else
                {
                    segs.Add(s);
                }
            }

            var end = Math.Max(cues[index].EndMs, next.EndMs);
            if (end - cues[index].StartMs > MaximumMs || segs.Count > MaxSegments) return null;
            var list = cues.ToList();
            list[index] = cues[index].With(endMs: end, segments: segs);
            list.RemoveAt(index + 1);
            return list;
        }

        public enum Edge { Start, End }

        /// <summary>Moves a cue's start or end, kept between its neighbours, at least <see cref="MinimumMs"/> and at most 10 minutes long. Null when nothing moved.</summary>
        public static IReadOnlyList<CaptionCue>? Nudge(IReadOnlyList<CaptionCue> cues, int index, Edge edge, int deltaMs)
        {
            if (!Has(cues, index)) return null;
            var cue = cues[index];
            CaptionCue moved;
            if (edge == Edge.Start)
            {
                var floor = Math.Max(Math.Max(0, index > 0 ? cues[index - 1].EndMs : 0), cue.EndMs - MaximumMs);
                moved = cue.With(startMs: Math.Min(Math.Max(floor, cue.StartMs + deltaMs), cue.EndMs - MinimumMs));
            }
            else
            {
                var ceiling = Math.Min(index + 1 < cues.Count ? cues[index + 1].StartMs : int.MaxValue, cue.StartMs + MaximumMs);
                moved = cue.With(endMs: Math.Max(Math.Min(ceiling, cue.EndMs + deltaMs), cue.StartMs + MinimumMs));
            }

            return moved.Equals(cue) ? null : Replace(cues, index, moved);
        }

        /// <summary>Sets a cue's start and end as typed, clamped like <see cref="Nudge"/>. Null when invalid.</summary>
        public static IReadOnlyList<CaptionCue>? SetTimes(IReadOnlyList<CaptionCue> cues, int index, int startMs, int endMs)
        {
            if (!Has(cues, index) || startMs < 0 || endMs - startMs < MinimumMs || endMs - startMs > MaximumMs) return null;
            if (index > 0 && startMs < cues[index - 1].EndMs) return null;
            if (index + 1 < cues.Count && endMs > cues[index + 1].StartMs) return null;
            return Replace(cues, index, cues[index].With(startMs: startMs, endMs: endMs));
        }

        public static IReadOnlyList<CaptionCue> Delete(IReadOnlyList<CaptionCue> cues, int index)
        {
            if (!Has(cues, index)) return cues;
            var list = cues.ToList();
            list.RemoveAt(index);
            return list;
        }

        /// <summary>The cues as saved: empty speech dropped, cues with no content left out (the server refuses them).</summary>
        public static IReadOnlyList<CaptionCue> ForSaving(IReadOnlyList<CaptionCue> cues)
        {
            var list = new List<CaptionCue>();
            foreach (var cue in cues)
            {
                var segs = new List<CaptionSegment>();
                foreach (var s in cue.Segments)
                {
                    if (s.Speech == null) segs.Add(s);
                    else if (s.Speech.Trim().Length > 0) segs.Add(CaptionSegment.Text(s.Speech.Trim()));
                }

                if (segs.Count > 0) list.Add(cue.With(segments: segs));
            }

            return list;
        }

        private static int Weight(IEnumerable<CaptionSegment> segs) => segs.Sum(s => Math.Max(1, (s.Speech ?? "").Length));

        private static bool Has(IReadOnlyList<CaptionCue> cues, int index) => index >= 0 && index < cues.Count;

        private static IReadOnlyList<CaptionCue> Replace(IReadOnlyList<CaptionCue> cues, int index, CaptionCue cue)
        {
            var list = cues.ToList();
            list[index] = cue;
            return list;
        }
    }
}
