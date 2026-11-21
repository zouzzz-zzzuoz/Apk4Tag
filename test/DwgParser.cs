using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using MauiColor = Microsoft.Maui.Graphics.Color;
using Microsoft.Maui.Graphics;

namespace test
{
    /// <summary>
    /// Parser for binary DWG files (R13–R2018) using ACadSharp.
    /// Produces a <see cref="TagModel"/> compatible with the existing viewer.
    /// </summary>
    public static class DwgParser
    {
        // AutoCAD Color Index (ACI) → MAUI Color (same table as DxfParser)
        static readonly MauiColor[] AciColors =
        {
            Colors.Black,    // 0 BYBLOCK
            Colors.Red,      // 1
            Colors.Yellow,   // 2
            Colors.Green,    // 3
            Colors.Cyan,     // 4
            Colors.Blue,     // 5
            Colors.Magenta,  // 6
            Colors.Black,    // 7 (white in AutoCAD, rendered black on white bg)
            Colors.DarkGray, // 8
            Colors.Gray      // 9
        };

        static MauiColor AciToColor(int aci)
        {
            int abs = Math.Abs(aci);
            return abs < AciColors.Length ? AciColors[abs] : Colors.Black;
        }

        /// <summary>Last error message from <see cref="Parse"/>. Null when parsing succeeded.</summary>
        public static string? LastError { get; private set; }

        public static TagModel Parse(Stream stream)
        {
            LastError = null;
            var model = new TagModel();
            try
            {
                // Always copy to MemoryStream: ACadSharp requires multiple read passes
                // and Android external-storage streams may not be seekable.
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                // AC1009 (DWG R12) is not supported by ACadSharp — use custom parser
                byte[] rawBytes = ms.ToArray();
                if (rawBytes.Length >= 6 && Encoding.ASCII.GetString(rawBytes, 0, 6) == "AC1009")
                    return ParseR12(rawBytes);

                CadDocument doc;
                using (var reader = new DwgReader(ms))
                    doc = reader.Read();

                // ── Build layer map ──────────────────────────────────────────
                model.Layers.Add(new TagLayer { Id = 0, Name = "0", Color = Colors.Black, Visible = true });
                var lm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["0"] = 0 };
                int nextId = 1;

                foreach (var layer in doc.Layers)
                {
                    if (lm.ContainsKey(layer.Name)) continue;

                    // Color: if ByLayer or index 7 (white) → render as black
                    MauiColor layerColor;
                    if (layer.Color.IsByLayer || layer.Color.IsByBlock || (int)layer.Color.Index == 7)
                        layerColor = Colors.Black;
                    else
                        layerColor = AciToColor((int)layer.Color.Index);

                    string nl = layer.Name.ToLowerInvariant();
                    bool isDim = nl.Contains("cot") || nl.Contains("dim");

                    bool dashed = false;
                    try
                    {
                        string? ltName = layer.LineType?.Name;
                        if (ltName != null)
                            dashed = ltName.Contains("DASH", StringComparison.OrdinalIgnoreCase)
                                  || ltName.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase)
                                  || ltName.Contains("CENTER", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }

                    model.Layers.Add(new TagLayer
                    {
                        Id = nextId,
                        Name = layer.Name,
                        Color = layerColor,
                        Visible = layer.IsOn,
                        LineType = dashed ? 1 : 0,
                        IsDimension = isDim
                    });
                    lm[layer.Name] = nextId++;
                }

                // ── Parse entities from model space ──────────────────────────
                foreach (var entity in doc.ModelSpace.Entities)
                    AddEntity(entity, model, lm, 0, 0, 1, 1, 0, 0);

                if (model.MinX == double.MaxValue)
                {
                    model.MinX = model.MinY = 0;
                    model.MaxX = model.MaxY = 1;
                }

                // ── Dimension color heuristic (same as DxfParser) ────────────
                var colorCounts = new Dictionary<int, int>();
                foreach (var ent in model.Entities)
                {
                    if (ent is TagText) continue;
                    if (ent.ColorIndex < 0) continue;
                    colorCounts.TryGetValue(ent.ColorIndex, out int cnt);
                    colorCounts[ent.ColorIndex] = cnt + 1;
                }
                int majorityColor = -1, maxCount = 0;
                foreach (var kv in colorCounts)
                    if (kv.Value > maxCount) { maxCount = kv.Value; majorityColor = kv.Key; }
                foreach (var kv in colorCounts)
                    if (kv.Key != majorityColor) model.DimensionColorIndices.Add(kv.Key);
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[DWG] {LastError}");
            }
            return model;
        }

        /// <summary>
        /// Recursively maps an ACadSharp entity to TagModel entities.
        /// ox/oy: INSERT translation offset, sx/sy: scale, rot: rotation in radians, depth: recursion guard.
        /// </summary>
        static void AddEntity(
            Entity ent, TagModel m, Dictionary<string, int> lm,
            double ox, double oy, double sx, double sy, double rot, int depth)
        {
            if (depth > 4) return;

            int layerId = 0;
            try { if (ent.Layer?.Name != null && lm.TryGetValue(ent.Layer.Name, out int lid)) layerId = lid; }
            catch { }

            int ci = -1;
            try { ci = (ent.Color.IsByLayer || ent.Color.IsByBlock) ? -1 : (int)ent.Color.Index; }
            catch { }

            // Transform helper: rotate then offset
            (double X, double Y) T(double px, double py)
            {
                if (rot == 0) return (ox + px * sx, oy + py * sy);
                double cos = Math.Cos(rot), sin = Math.Sin(rot);
                return (ox + (px * sx * cos - py * sy * sin),
                        oy + (px * sx * sin + py * sy * cos));
            }

            try
            {
                switch (ent)
                {
                    case ACadSharp.Entities.Line l:
                    {
                        var (x1, y1) = T(l.StartPoint.X, l.StartPoint.Y);
                        var (x2, y2) = T(l.EndPoint.X, l.EndPoint.Y);
                        m.Entities.Add(new TagLine
                        {
                            X1 = (float)x1, Y1 = (float)y1,
                            X2 = (float)x2, Y2 = (float)y2,
                            LayerId = layerId, ColorIndex = ci
                        });
                        m.AddPoint(x1, y1); m.AddPoint(x2, y2);
                        break;
                    }

                    // Arc must come before Circle because Arc inherits from Circle in ACadSharp
                    case ACadSharp.Entities.Arc a:
                    {
                        var (cx, cy) = T(a.Center.X, a.Center.Y);
                        float r = (float)(a.Radius * Math.Max(Math.Abs(sx), Math.Abs(sy)));
                        // ACadSharp stores angles in radians; TagArc uses degrees
                        double rotDeg = rot * 180.0 / Math.PI;
                        double startDeg = a.StartAngle * 180.0 / Math.PI + rotDeg;
                        double endDeg = a.EndAngle * 180.0 / Math.PI + rotDeg;
                        double sweep = endDeg - startDeg;
                        if (sweep <= 0) sweep += 360.0;
                        m.Entities.Add(new TagArc
                        {
                            CX = (float)cx, CY = (float)cy, R = r,
                            StartAngle = (float)startDeg, SweepAngle = (float)sweep,
                            LayerId = layerId, ColorIndex = ci
                        });
                        m.AddPoint(cx - r, cy - r); m.AddPoint(cx + r, cy + r);
                        break;
                    }

                    case ACadSharp.Entities.Circle c:
                    {
                        var (cx, cy) = T(c.Center.X, c.Center.Y);
                        float r = (float)(c.Radius * Math.Max(Math.Abs(sx), Math.Abs(sy)));
                        m.Entities.Add(new TagCircle
                        {
                            CX = (float)cx, CY = (float)cy, R = r,
                            LayerId = layerId, ColorIndex = ci
                        });
                        m.AddPoint(cx - r, cy - r); m.AddPoint(cx + r, cy + r);
                        break;
                    }

                    case ACadSharp.Entities.Point pt:
                    {
                        var (px, py) = T(pt.Location.X, pt.Location.Y);
                        m.Entities.Add(new TagPoint
                        {
                            X = (float)px, Y = (float)py,
                            LayerId = layerId, ColorIndex = ci
                        });
                        m.AddPoint(px, py);
                        break;
                    }

                    case ACadSharp.Entities.TextEntity txt:
                    {
                        if (!string.IsNullOrWhiteSpace(txt.Value))
                        {
                            var (px, py) = T(txt.InsertPoint.X, txt.InsertPoint.Y);
                            m.Entities.Add(new TagText
                            {
                                X = (float)px, Y = (float)py,
                                Text = txt.Value,
                                Size = (float)Math.Max(txt.Height * Math.Abs(sy), 1),
                                LayerId = layerId, ColorIndex = ci
                            });
                            m.AddPoint(px, py);
                        }
                        break;
                    }

                    case ACadSharp.Entities.MText mt:
                    {
                        var raw = StripMTextCodes(mt.Value ?? "");
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var (px, py) = T(mt.InsertPoint.X, mt.InsertPoint.Y);
                            m.Entities.Add(new TagText
                            {
                                X = (float)px, Y = (float)py,
                                Text = raw,
                                Size = (float)Math.Max(mt.Height * Math.Abs(sy), 1),
                                LayerId = layerId, ColorIndex = ci
                            });
                            m.AddPoint(px, py);
                        }
                        break;
                    }

                    case ACadSharp.Entities.LwPolyline poly:
                    {
                        var verts = poly.Vertices;
                        int count = poly.IsClosed ? verts.Count : Math.Max(0, verts.Count - 1);
                        for (int i = 0; i < count; i++)
                        {
                            int j = (i + 1) % verts.Count;
                            var (x1, y1) = T(verts[i].Location.X, verts[i].Location.Y);
                            var (x2, y2) = T(verts[j].Location.X, verts[j].Location.Y);
                            m.Entities.Add(new TagLine
                            {
                                X1 = (float)x1, Y1 = (float)y1,
                                X2 = (float)x2, Y2 = (float)y2,
                                LayerId = layerId, ColorIndex = ci
                            });
                            m.AddPoint(x1, y1); m.AddPoint(x2, y2);
                        }
                        break;
                    }

                    case ACadSharp.Entities.Polyline2D poly2d:
                    {
                        var verts = poly2d.Vertices.ToList();
                        int segCount = poly2d.IsClosed ? verts.Count : Math.Max(0, verts.Count - 1);
                        for (int i = 0; i < segCount; i++)
                        {
                            int j = (i + 1) % verts.Count;
                            var (x1, y1) = T(verts[i].Location.X, verts[i].Location.Y);
                            var (x2, y2) = T(verts[j].Location.X, verts[j].Location.Y);
                            m.Entities.Add(new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2, LayerId = layerId, ColorIndex = ci });
                            m.AddPoint(x1, y1); m.AddPoint(x2, y2);
                        }
                        break;
                    }

                    case ACadSharp.Entities.Polyline3D poly3d:
                    {
                        var verts = poly3d.Vertices.ToList();
                        int segCount = poly3d.IsClosed ? verts.Count : Math.Max(0, verts.Count - 1);
                        for (int i = 0; i < segCount; i++)
                        {
                            int j = (i + 1) % verts.Count;
                            var (x1, y1) = T(verts[i].Location.X, verts[i].Location.Y);
                            var (x2, y2) = T(verts[j].Location.X, verts[j].Location.Y);
                            m.Entities.Add(new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2, LayerId = layerId, ColorIndex = ci });
                            m.AddPoint(x1, y1); m.AddPoint(x2, y2);
                        }
                        break;
                    }

                    case ACadSharp.Entities.Spline spl:
                    {
                        // Use FitPoints when available (more accurate), otherwise ControlPoints
                        var pts = spl.FitPoints.Count > 0
                            ? spl.FitPoints
                            : spl.ControlPoints;
                        for (int i = 0; i < pts.Count - 1; i++)
                        {
                            var (x1, y1) = T(pts[i].X, pts[i].Y);
                            var (x2, y2) = T(pts[i + 1].X, pts[i + 1].Y);
                            m.Entities.Add(new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2, LayerId = layerId, ColorIndex = ci });
                            m.AddPoint(x1, y1); m.AddPoint(x2, y2);
                        }
                        break;
                    }

                    case ACadSharp.Entities.Ellipse ell:
                    {
                        var (ecx, ecy) = T(ell.Center.X, ell.Center.Y);
                        // MajorAxisEndPoint is the semi-major axis vector relative to center
                        double eax = ell.MajorAxisEndPoint.X * sx;
                        double eay = ell.MajorAxisEndPoint.Y * sy;
                        double ea = Math.Sqrt(eax * eax + eay * eay);
                        if (ea < 1e-9) break;
                        double eb = ea * ell.RadiusRatio;
                        double etheta = Math.Atan2(eay, eax) + rot;
                        double eStart = ell.StartParameter;
                        double eEnd = ell.EndParameter;
                        if (eEnd <= eStart) eEnd += 2 * Math.PI;
                        int segs = Math.Max(32, (int)Math.Ceiling((eEnd - eStart) / (2 * Math.PI) * 64));
                        double estep = (eEnd - eStart) / segs;
                        for (int i = 0; i < segs; i++)
                        {
                            double t1 = eStart + estep * i;
                            double t2 = eStart + estep * (i + 1);
                            double px1 = ecx + ea * Math.Cos(t1) * Math.Cos(etheta) - eb * Math.Sin(t1) * Math.Sin(etheta);
                            double py1 = ecy + ea * Math.Cos(t1) * Math.Sin(etheta) + eb * Math.Sin(t1) * Math.Cos(etheta);
                            double px2 = ecx + ea * Math.Cos(t2) * Math.Cos(etheta) - eb * Math.Sin(t2) * Math.Sin(etheta);
                            double py2 = ecy + ea * Math.Cos(t2) * Math.Sin(etheta) + eb * Math.Sin(t2) * Math.Cos(etheta);
                            m.Entities.Add(new TagLine { X1 = (float)px1, Y1 = (float)py1, X2 = (float)px2, Y2 = (float)py2, LayerId = layerId, ColorIndex = ci });
                            m.AddPoint(px1, py1); m.AddPoint(px2, py2);
                        }
                        break;
                    }

                    case ACadSharp.Entities.Insert ins:
                    {
                        var blk = ins.Block;
                        if (blk == null) break;
                        var (bx, by) = T(ins.InsertPoint.X, ins.InsertPoint.Y);
                        double newSx = sx * ins.XScale;
                        double newSy = sy * ins.YScale;
                        double newRot = rot + ins.Rotation;
                        foreach (var be in blk.Entities)
                            AddEntity(be, m, lm, bx, by, newSx, newSy, newRot, depth + 1);
                        break;
                    }
                }
            }
            catch { }
        }

        // ── DWG R12 (AC1009) custom parser ────────────────────────────────────────
        static TagModel ParseR12(byte[] data)
        {
            var model = new TagModel();
            model.Layers.Add(new TagLayer { Id = 0, Name = "0", Color = Colors.Black, Visible = true });
            var diag = new System.Text.StringBuilder();
            try
            {
                if (data.Length < 0x1A) { LastError = "R12: fichier trop petit"; return model; }

                diag.Append($"sz={data.Length}");
                // Capture raw header bytes [0x06..0x17] — covers all tested section-table offsets
                diag.Append(" hdr[");
                for (int _i = 6; _i < Math.Min(0x18, data.Length); _i++)
                    diag.Append(data[_i].ToString("X2"));
                diag.Append("]");

                int preferredAddr = -1, preferredSize = 0;
                var fallbackSections = new List<(int addr, int size)>();

                // DWG R12 canonical: count at 0x0B, section records at 0x0C (9 bytes each: type(1)+addr(4LE)+size(4LE))
                ReadSectionTable(data, 0x0B, 0x0C, 1, ref preferredAddr, ref preferredSize, fallbackSections);
                // R12 compact format: count at 0x0C, 5-byte records type(1)+addr(2LE)+size(2LE) at 0x0D
                if (preferredAddr < 0 && fallbackSections.Count == 0)
                    ReadSectionTableSmall(data, 0x0C, 0x0D, ref preferredAddr, ref preferredSize, fallbackSections);
                // Alternative layouts for R12 variants
                if (preferredAddr < 0 && fallbackSections.Count == 0)
                    ReadSectionTable(data, 0x12, 0x13, 1, ref preferredAddr, ref preferredSize, fallbackSections);
                if (preferredAddr < 0 && fallbackSections.Count == 0)
                    ReadSectionTable(data, 0x15, 0x17, 2, ref preferredAddr, ref preferredSize, fallbackSections);
                if (preferredAddr < 0 && fallbackSections.Count == 0)
                    ReadSectionTable(data, 0x14, 0x15, 1, ref preferredAddr, ref preferredSize, fallbackSections);
                if (preferredAddr < 0 && fallbackSections.Count == 0)
                    ReadSectionTable(data, 0x13, 0x14, 1, ref preferredAddr, ref preferredSize, fallbackSections);

                diag.Append($" pref={preferredAddr:X} fb={fallbackSections.Count}");

                fallbackSections.Sort((a, b) => b.size.CompareTo(a.size));
                var sections = new List<(int addr, int size)>();
                if (preferredAddr >= 0) sections.Add((preferredAddr, preferredSize));
                sections.AddRange(fallbackSections);

                // Log first bytes of each candidate section start + entity type scan
                foreach (var sec in sections)
                {
                    diag.Append($" sec={sec.addr:X}+{sec.size:X}[");
                    for (int _i = sec.addr; _i < Math.Min(sec.addr + 32, data.Length); _i++)
                        diag.Append(data[_i].ToString("X2"));
                    diag.Append("]");
                    // Scan first entity records (1-byte type format) for diagnostic
                    diag.Append(" etypes=[");
                    int tPos = sec.addr;
                    for (int t = 0; t < 15 && tPos + 3 <= sec.addr + sec.size; t++)
                    {
                        int et = data[tPos];
                        int es = (int)BitConverter.ToUInt16(data, tPos + 1);
                        if (et == 0 || es < 1 || es > 4096) break;
                        if (t > 0) diag.Append(',');
                        diag.Append($"{et}:{es}");
                        tPos += 3 + es;
                        if (tPos > sec.addr + sec.size) break;
                    }
                    diag.Append("]");
                }

                // Try each section with preamble offsets AND multiple parsing modes
                foreach (var sec in sections)
                {
                    if (model.Entities.Count > 0) break;
                    foreach (int preamble in new[] { 0, 4, 8, 16, 24, 32 })
                    {
                        if (model.Entities.Count > 0) break;
                        int sStart = sec.addr + preamble;
                        int sEnd   = sec.addr + sec.size;
                        if (sStart >= sEnd) continue;
                        ParseR12Entities(data, sStart, sEnd, model, byteEntityType: true,  skipOptionals: false);
                        if (model.Entities.Count > 0) break;
                        ParseR12Entities(data, sStart, sEnd, model, byteEntityType: true,  skipOptionals: true);
                        if (model.Entities.Count > 0) break;
                        ParseR12Entities(data, sStart, sEnd, model, byteEntityType: false, skipOptionals: false);
                        if (model.Entities.Count > 0) break;
                        ParseR12Entities(data, sStart, sEnd, model, byteEntityType: false, skipOptionals: true);
                    }
                }

                // Raw scan disabled — it generates false positives (entities with valid-looking
                // coordinates but garbage data) that cause a blank viewer with no popup.
                // Section-based parsing must succeed; the diagnostic popup will indicate what was found.
                // if (model.Entities.Count == 0)
                //     ParseR12RawScan(data, model);

                // Reject suspiciously low entity count (likely false positives from random data)
                if (model.Entities.Count > 0 && model.Entities.Count < 3)
                {
                    diag.Append($" lowEnts={model.Entities.Count}_rejected");
                    model.Entities.Clear();
                    model.MinX = double.MaxValue; model.MinY = double.MaxValue;
                    model.MaxX = double.MinValue; model.MaxY = double.MinValue;
                }

                // Sanity check: reject entities whose bounding box is absurdly large
                // (typical sign of raw-scan false positives with garbage coordinates)
                if (model.Entities.Count > 0 && model.MinX != double.MaxValue)
                {
                    double bboxW = model.MaxX - model.MinX;
                    double bboxH = model.MaxY - model.MinY;
                    if (bboxW > 1e8 || bboxH > 1e8)
                    {
                        diag.Append($" rawBbox={bboxW:G3}x{bboxH:G3}_rejected");
                        model.Entities.Clear();
                        model.MinX = double.MaxValue;
                        model.MinY = double.MaxValue;
                        model.MaxX = double.MinValue;
                        model.MaxY = double.MinValue;
                    }
                }

                diag.Append($" ents={model.Entities.Count}");
            }
            catch (Exception ex) { diag.Append($" EX:{ex.GetType().Name}:{ex.Message}"); }

            // Always store diagnostic so it appears in the error dialog
            LastError = diag.ToString();
            if (model.MinX == double.MaxValue) { model.MinX = model.MinY = 0; model.MaxX = model.MaxY = 1; }
            return model;
        }

        static void ReadSectionTable(byte[] data, int countOff, int tableStart, int countBytes,
            ref int preferredAddr, ref int preferredSize, List<(int addr, int size)> fallback)
        {
            if (countOff >= data.Length) return;
            int cnt = countBytes == 1
                ? Math.Min((int)data[countOff], 8)
                : Math.Min((int)BitConverter.ToUInt16(data, countOff), 16);
            int off = tableStart;
            for (int i = 0; i < cnt && off + 9 <= data.Length; i++, off += 9)
            {
                byte secType = data[off];
                int secAddr = BitConverter.ToInt32(data, off + 1);
                int secSize = BitConverter.ToInt32(data, off + 5);
                if (secAddr <= 0 || secAddr >= data.Length || secSize <= 0) continue;
                int safeSize = Math.Min(secSize, data.Length - secAddr);
                if (safeSize < 8) continue;
                // Type 1 = ENTITIES section (primary target in R12)
                // All other non-zero types kept as fallback (handles, blocks, extra, etc.)
                if (secType == 1) { preferredAddr = secAddr; preferredSize = safeSize; }
                else if (secType > 1) fallback.Add((secAddr, safeSize));
            }
        }

        static void ReadSectionTableSmall(byte[] data, int countOff, int tableStart,
            ref int preferredAddr, ref int preferredSize, List<(int addr, int size)> fallback)
        {
            // 5-byte record format: type(1) + addr(2LE) + size(2LE)
            // Used by some R12 (AC1009) variants where section addresses fit in 16 bits
            if (countOff >= data.Length) return;
            int cnt = Math.Min((int)data[countOff], 8);
            int off = tableStart;
            for (int i = 0; i < cnt && off + 5 <= data.Length; i++, off += 5)
            {
                byte secType = data[off];
                int secAddr = (int)BitConverter.ToUInt16(data, off + 1);
                int secSize = (int)BitConverter.ToUInt16(data, off + 3);
                if (secAddr <= 0 || secAddr >= data.Length || secSize <= 0) continue;
                int safeSize = Math.Min(secSize, data.Length - secAddr);
                if (safeSize < 8) continue;
                if (secType == 1) { preferredAddr = secAddr; preferredSize = safeSize; }
                else fallback.Add((secAddr, safeSize));
            }
        }

        static void ParseR12RawScan(byte[] data, TagModel model)
        {
            // Byte-by-byte scan: find any position where the data looks like a valid
            // entity start (known type + plausible size + valid coordinates after the
            // common 3-byte header).  Low false-positive rate because IEEE-754 doubles
            // with |x|<1e10 and |y|<1e10 are statistically rare at arbitrary offsets.
            for (int start = 0x10; start < data.Length - 60; start++)
            {
                // — 1-byte entity-type probe —
                int et1 = data[start];
                if (et1 == 1 || et1 == 3 || et1 == 7 || et1 == 8 || et1 == 9)
                {
                    int sz1 = BitConverter.ToUInt16(data, start + 1);
                    if (sz1 >= 20 && sz1 <= 800)
                    {
                        int dp1  = start + 3 + 3; // hdrSize(3) + opts(1)+layer(2)
                        int end1 = start + 3 + sz1;
                        if (dp1 + 16 <= end1 && end1 <= data.Length)
                        {
                            double px = BitConverter.ToDouble(data, dp1);
                            double py = BitConverter.ToDouble(data, dp1 + 8);
                            if (R12Coord(px, py))
                            {
                                int prev = model.Entities.Count;
                                ParseR12Entities(data, start, data.Length, model, byteEntityType: true, skipOptionals: false);
                                if (model.Entities.Count > prev) return;
                                ParseR12Entities(data, start, data.Length, model, byteEntityType: true, skipOptionals: true);
                                if (model.Entities.Count > prev) return;
                            }
                        }
                    }
                }

                // — 2-byte entity-type probe —
                if (start + 4 < data.Length)
                {
                    int et2 = (int)(short)BitConverter.ToInt16(data, start);
                    if (et2 == 1 || et2 == 3 || et2 == 7 || et2 == 8 || et2 == 9)
                    {
                        int sz2 = BitConverter.ToUInt16(data, start + 2);
                        if (sz2 >= 20 && sz2 <= 800)
                        {
                            int dp2  = start + 4 + 3; // hdrSize(4) + opts(1)+layer(2)
                            int end2 = start + 4 + sz2;
                            if (dp2 + 16 <= end2 && end2 <= data.Length)
                            {
                                double px = BitConverter.ToDouble(data, dp2);
                                double py = BitConverter.ToDouble(data, dp2 + 8);
                                if (R12Coord(px, py))
                                {
                                    int prev = model.Entities.Count;
                                    ParseR12Entities(data, start, data.Length, model, byteEntityType: false, skipOptionals: false);
                                    if (model.Entities.Count > prev) return;
                                    ParseR12Entities(data, start, data.Length, model, byteEntityType: false, skipOptionals: true);
                                    if (model.Entities.Count > prev) return;
                                }
                            }
                        }
                    }
                }
            }
        }

        static void ParseR12Entities(byte[] data, int start, int end, TagModel model,
            bool byteEntityType = false, bool skipOptionals = false)
        {
            int pos = start;
            List<PointF>? polyVerts = null;
            int hdrSize = byteEntityType ? 3 : 4;

            while (pos + hdrSize <= end)
            {
                int entityType = byteEntityType
                    ? (int)data[pos]
                    : (int)(short)BitConverter.ToInt16(data, pos);
                int entitySize = byteEntityType
                    ? (int)BitConverter.ToUInt16(data, pos + 1)
                    : (int)BitConverter.ToUInt16(data, pos + 2);
                int entityEnd = pos + hdrSize + entitySize;
                if (entityType == 0 || entityType == -1 || entityEnd > end) break;
                if (entitySize < 1 || entitySize > 4096) break;

                int dp = pos + hdrSize; // start of entity data
                try
                {
                    if (dp + 3 <= entityEnd)
                    {
                        if (skipOptionals)
                        {
                            // Skip fixed header: opts(1) + layer_ref(2), ignore optional fields
                            dp += 3;
                        }
                        else
                        {
                            // Common entity header (DWG R12 spec):
                            //   opts(1) + layer_ref(2) + optional fields
                            // opts bit 0x01 = has linetype ref   (+2 bytes int16)
                            // opts bit 0x04 = has thickness      (+8 bytes double)
                            // opts bit 0x40 = has extrusion      (+24 bytes, 3 doubles)
                            // opts bit 0x20 = has color          (+1 byte)
                            int opts = data[dp]; dp++;
                            dp += 2; // layer index — use default layer 0
                            if ((opts & 0x01) != 0 && dp + 2 <= entityEnd) dp += 2;  // linetype ref
                            if ((opts & 0x04) != 0 && dp + 8 <= entityEnd) dp += 8;  // thickness
                            if ((opts & 0x40) != 0 && dp + 24 <= entityEnd) dp += 24; // extrusion
                            if ((opts & 0x20) != 0 && dp < entityEnd) dp += 1;        // color
                        }

                        switch (entityType)
                        {
                            case 1: // LINE: x1(8)+y1(8)+z1(8)+x2(8)+y2(8)+z2(8)
                                if (dp + 48 <= entityEnd)
                                {
                                    double x1 = R12Dbl(data, dp), y1 = R12Dbl(data, dp + 8);
                                    double x2 = R12Dbl(data, dp + 24), y2 = R12Dbl(data, dp + 32);
                                    if (R12Coord(x1, y1) && R12Coord(x2, y2))
                                    {
                                        model.Entities.Add(new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2 });
                                        model.AddPoint(x1, y1); model.AddPoint(x2, y2);
                                    }
                                }
                                break;

                            case 3: // CIRCLE: cx(8)+cy(8)+cz(8)+r(8)
                                if (dp + 32 <= entityEnd)
                                {
                                    double cx = R12Dbl(data, dp), cy = R12Dbl(data, dp + 8);
                                    double r = R12Dbl(data, dp + 24);
                                    if (R12Coord(cx, cy) && r > 0 && r < 1e9)
                                    {
                                        model.Entities.Add(new TagCircle { CX = (float)cx, CY = (float)cy, R = (float)r });
                                        model.AddPoint(cx - r, cy - r); model.AddPoint(cx + r, cy + r);
                                    }
                                }
                                break;

                            case 8: // ARC: cx(8)+cy(8)+cz(8)+r(8)+startAngle(8)+endAngle(8)
                                    // OpenDWG R12 spec: type 7=TEXT, 8=ARC, 9=TRACE
                                if (dp + 48 <= entityEnd)
                                {
                                    double cx = R12Dbl(data, dp), cy = R12Dbl(data, dp + 8);
                                    double r = R12Dbl(data, dp + 24);
                                    double startRad = R12Dbl(data, dp + 32), endRad = R12Dbl(data, dp + 40);
                                    if (R12Coord(cx, cy) && r > 0 && r < 1e9)
                                    {
                                        double startDeg = startRad * 180.0 / Math.PI;
                                        double endDeg = endRad * 180.0 / Math.PI;
                                        double sweep = endDeg - startDeg;
                                        if (sweep <= 0) sweep += 360.0;
                                        model.Entities.Add(new TagArc { CX = (float)cx, CY = (float)cy, R = (float)r, StartAngle = (float)startDeg, SweepAngle = (float)sweep });
                                        model.AddPoint(cx - r, cy - r); model.AddPoint(cx + r, cy + r);
                                    }
                                }
                                break;

                            case 18: // POLYLINE (0x12) — start vertex accumulation
                                polyVerts = new List<PointF>();
                                break;

                            case 19: // VERTEX (0x13): x(8)+y(8)+...
                                if (polyVerts != null && dp + 16 <= entityEnd)
                                {
                                    double vx = R12Dbl(data, dp), vy = R12Dbl(data, dp + 8);
                                    if (R12Coord(vx, vy))
                                        polyVerts.Add(new PointF((float)vx, (float)vy));
                                }
                                break;

                            case 17: // SEQEND (0x11) — flush polyline segments
                                if (polyVerts != null && polyVerts.Count >= 2)
                                {
                                    for (int i = 0; i < polyVerts.Count - 1; i++)
                                    {
                                        model.Entities.Add(new TagLine { X1 = polyVerts[i].X, Y1 = polyVerts[i].Y, X2 = polyVerts[i + 1].X, Y2 = polyVerts[i + 1].Y });
                                        model.AddPoint(polyVerts[i].X, polyVerts[i].Y);
                                        model.AddPoint(polyVerts[i + 1].X, polyVerts[i + 1].Y);
                                    }
                                    // Close if first and last vertices differ (closed polyline)
                                    var pFirst = polyVerts[0]; var pLast = polyVerts[^1];
                                    float ddx = pFirst.X - pLast.X, ddy = pFirst.Y - pLast.Y;
                                    if (ddx * ddx + ddy * ddy > 1e-6f)
                                    {
                                        model.Entities.Add(new TagLine { X1 = pLast.X, Y1 = pLast.Y, X2 = pFirst.X, Y2 = pFirst.Y });
                                        model.AddPoint(pLast.X, pLast.Y); model.AddPoint(pFirst.X, pFirst.Y);
                                    }
                                }
                                polyVerts = null;
                                break;
                        }
                    }
                }
                catch { }

                pos = entityEnd;
            }
        }

        static bool R12Coord(double x, double y) =>
            !double.IsNaN(x) && !double.IsInfinity(x) && Math.Abs(x) < 1e10 &&
            !double.IsNaN(y) && !double.IsInfinity(y) && Math.Abs(y) < 1e10;

        static double R12Dbl(byte[] d, int p) =>
            BitConverter.IsLittleEndian
                ? BitConverter.ToDouble(d, p)
                : BitConverter.ToDouble(new byte[] { d[p+7], d[p+6], d[p+5], d[p+4], d[p+3], d[p+2], d[p+1], d[p] }, 0);

        /// <summary>Strips MText formatting codes ({\...}, \P, \pxi, etc.).</summary>
        static string StripMTextCodes(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    if (next == 'P' || next == 'p') { sb.Append('\n'); i += 2; continue; }
                    // skip formatting sequence: \X;  or \X{...}
                    i += 2;
                    if (i < s.Length && s[i] == ';') i++;
                    continue;
                }
                if (s[i] == '{' || s[i] == '}') { i++; continue; }
                sb.Append(s[i++]);
            }
            return sb.ToString().Trim();
        }
    }
}
