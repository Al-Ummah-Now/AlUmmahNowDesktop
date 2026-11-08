# D9 finding: GTK 4 through Gir.Core

**Verdict: keep GTK 4 + libadwaita with Gir.Core 0.8.1. Do not move to Avalonia.** The editor (19.5) is
feasible, but it needs a thin native shim of our own for the items below.

Evidence: `spikes/D9.GirCore`, run in CI on Ubuntu 26.04 (GTK 4.22, libadwaita 1.9).

| Check | Result |
|---|---|
| QA HLS through `gtk4paintablesink` into `Gtk.Picture` | Pass: ~970 frames, reached 43.9 s after the seek |
| Flushing seek at 1.5x | Pass |
| RTL (mirrored window, list, header; shaped Arabic with mixed Latin) | Pass, confirmed in screenshots |
| `Gtk.ListView` over a managed `GObject` subclass, 2000 rows | Pass |
| Forced GC every 200 ms while media and lists run | 124 rounds, no crash |
| Timeline with cairo `DrawingArea`, 2000 clips | Pass: ~0.8 ms per draw |
| Timeline with a `snapshot` vfunc, 2000 clips | Pass: ~0.15 ms per draw, needs a workaround |

## Binding gaps found

1. **No managed `Gtk.Widget.snapshot` override.** It works by writing to the class struct through
   `Gtk.Internal.WidgetClassData` and calling `gtk_snapshot_*` directly. An upgrade could break this.
2. **`Gst.Message.ParseError`, `ParseWarning` and `ParseInfo` are not bound.** A small P/Invoke covers them.
3. **Unknown GTypes that implement an interface** may need a non-public `PaintableHelper` constructor.
   In these runs the binding returned the helper directly, so the fallback was not exercised.
4. **No GStreamer Editing Services package** (`GirCore.Ges` does not exist). The editor must drive
   `compositor` pipelines itself, generate a GES binding, or render through `aun-media`.

## What this does not prove

- Run under Xvfb with `GSK_RENDERER=cairo`, so the draw timings are not GPU numbers and hardware decode was not used.
- Gesture code compiled and attached, but no synthetic drag was delivered.
- The stress test lasted 25 seconds, so long-session leaks are untested.
- Gir.Core is pre-1.0 and its API can change: pin it and wrap it behind our own types.

## Revisit if

The GES route proves unworkable for 19.5, or the `snapshot` workaround breaks and upstream declines a fix.
