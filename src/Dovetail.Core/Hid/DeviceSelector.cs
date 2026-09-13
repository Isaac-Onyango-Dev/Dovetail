namespace Dovetail.Core;

/// <summary>
/// Picks the HID collection that actually carries the pad.
///
/// Stage 1 finding 1.8: this adapter exposes two joystick collections from one USB endpoint
/// and both stream idle reports at roughly 100 Hz whether or not a pad is attached to that
/// port. Choosing by enumeration index is therefore a coin flip, and choosing wrong is what
/// parked x360ce on an empty port. Selection is by observed activity, with the report id in
/// byte 0 as the stable identifier once a choice is made.
/// </summary>
internal static class DeviceSelector
{
    internal sealed record Choice(
        HidCollectionInfo Info,
        byte ReportId,
        byte[] RestState,
        string Reason);

    /// <summary>
    /// Watches every candidate collection and returns the one that moves. If none moves
    /// within <paramref name="probeMs"/>, returns null and the caller must prompt the user
    /// to press something.
    /// </summary>
    internal static Choice? SelectByActivity(
        IReadOnlyList<HidCollectionInfo> candidates, int probeMs, Action<string>? log = null)
    {
        var readers = new List<(HidCollectionInfo info, HidReader reader)>();
        try
        {
            foreach (var c in candidates)
            {
                try { readers.Add((c, new HidReader(c.DevicePath, c.Caps.InputReportByteLength))); }
                catch (Exception ex) { log?.Invoke($"  {c.CollectionTag}: cannot open, {ex.Message}"); }
            }
            if (readers.Count == 0) return null;

            // settle on a rest state per collection first
            var rest = new Dictionary<string, byte[]>();
            var settle = DateTime.UtcNow.AddMilliseconds(800);
            while (DateTime.UtcNow < settle)
                foreach (var (info, reader) in readers)
                {
                    var r = reader.Read(20);
                    if (r is { Length: > 0 }) rest[info.CollectionTag] = r;
                }

            if (readers.Count == 1 && rest.TryGetValue(readers[0].info.CollectionTag, out var only))
                return new Choice(readers[0].info, only.Length > 0 ? only[0] : (byte)0, only,
                                  "only one collection present");

            // now look for movement
            var deadline = DateTime.UtcNow.AddMilliseconds(probeMs);
            var moved = new Dictionary<string, int>();
            while (DateTime.UtcNow < deadline)
            {
                foreach (var (info, reader) in readers)
                {
                    var r = reader.Read(20);
                    if (r is null || r.Length == 0) continue;
                    if (!rest.TryGetValue(info.CollectionTag, out var baseline)) { rest[info.CollectionTag] = r; continue; }

                    int delta = 0;
                    for (int k = 0; k < Math.Min(r.Length, baseline.Length); k++)
                        delta += Math.Abs(r[k] - baseline[k]);
                    if (delta > 0)
                        moved[info.CollectionTag] = moved.GetValueOrDefault(info.CollectionTag) + delta;
                }
                if (moved.Count > 0 && moved.Values.Max() > 40) break;
            }

            if (moved.Count == 0) return null;

            string winner = moved.OrderByDescending(kv => kv.Value).First().Key;
            var chosen = readers.First(r => r.info.CollectionTag == winner).info;
            var restState = rest.GetValueOrDefault(winner, []);
            return new Choice(chosen, restState.Length > 0 ? restState[0] : (byte)0, restState,
                              $"only collection that moved (activity score {moved[winner]})");
        }
        finally
        {
            foreach (var (_, r) in readers) r.Dispose();
        }
    }

    /// <summary>
    /// Resolves a collection by the report id Stage 1 recorded, with no user action needed.
    /// This is the path Dovetail itself uses at runtime once calibration has pinned the port.
    /// </summary>
    internal static HidCollectionInfo? SelectByReportId(
        IReadOnlyList<HidCollectionInfo> candidates, byte reportId)
    {
        foreach (var c in candidates)
        {
            HidReader? r = null;
            try
            {
                r = new HidReader(c.DevicePath, c.Caps.InputReportByteLength);
                var rpt = r.Read(200);
                if (rpt is { Length: > 0 } && rpt[0] == reportId) return c;
            }
            catch { /* try the next candidate */ }
            finally { r?.Dispose(); }
        }
        return null;
    }
}
