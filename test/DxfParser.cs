using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Maui.Graphics;

namespace test
{
    /// <summary>
    /// Parser for text-based DXF files. Produces a <see cref="TagModel"/> containing
    /// LINE, CIRCLE, ARC, POINT, TEXT, LWPOLYLINE, POLYLINE, ELLIPSE, SPLINE entities.
    /// </summary>
    public static class DxfParser
    {
        // AutoCAD Color Index (ACI) → MAUI Color mapping (standard 10 colors)
        static readonly Color[] AciColors =
        {
            Colors.Black,    // 0 BYBLOCK
            Colors.Red,      // 1
            Colors.Yellow,   // 2
            Colors.Green,    // 3
            Colors.Cyan,     // 4
            Colors.Blue,     // 5
            Colors.Magenta,  // 6
            Colors.Black,    // 7 (white in AutoCAD, rendered as black on white bg)
            Colors.DarkGray, // 8
            Colors.Gray      // 9
        };

        static Color AciToColor(int aci)
        {
            int abs = Math.Abs(aci);
            if (abs < AciColors.Length) return AciColors[abs];
            return Colors.Black;
        }

        public static TagModel Parse(Stream stream)
        {
            var pairs = ReadPairs(stream);
            var model = new TagModel();
            model.Layers.Add(new TagLayer { Id = 0, Name = "0", Color = Colors.Black, Visible = true });

            int layerIdCounter = 1;
            var layerNameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["0"] = 0 };

            int i = 0;

            // ── Pass 1: parse TABLES section for LAYER definitions ──
            while (i < pairs.Count)
            {
                var (code, value) = pairs[i];
                if (code == 0 && value == "SECTION" && i + 1 < pairs.Count &&
                    pairs[i + 1].code == 2 && pairs[i + 1].value == "TABLES")
                {
                    i += 2;
                    while (i < pairs.Count)
                    {
                        var (tc, tv) = pairs[i];
                        if (tc == 0 && tv == "ENDSEC") break;
                        if (tc == 0 && tv == "LAYER")
                        {
                            i++;
                            string layerName = "Default";
                            int aci = 7;
                            int flags = 0;
                            string linetype = "CONTINUOUS";
                            while (i < pairs.Count && pairs[i].code != 0)
                            {
                                switch (pairs[i].code)
                                {
                                    case 2: layerName = pairs[i].value; break;
                                    case 62: int.TryParse(pairs[i].value, out aci); break;
                                    case 70: int.TryParse(pairs[i].value, out flags); break;
                                    case 6: linetype = pairs[i].value; break;
                                }
                                i++;
                            }
                            bool visible = aci >= 0;
                            int absAci = Math.Abs(aci);
                            bool isDashed = linetype != null &&
                                (linetype.Contains("DASH", StringComparison.OrdinalIgnoreCase) ||
                                 linetype.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase) ||
                                 linetype.Contains("CENTER", StringComparison.OrdinalIgnoreCase));
                            var nameLower = layerName.ToLowerInvariant();
                            bool isDimension = nameLower.Contains("cot") || nameLower.Contains("dimension") || nameLower.Contains("dim");
                            int lid = layerIdCounter++;
                            layerNameToId[layerName] = lid;
                            model.Layers.Add(new TagLayer
                            {
                                Id = lid,
                                Name = layerName,
                                Color = AciToColor(absAci),
                                Visible = visible,
                                LineType = isDashed ? 1 : 0,
                                IsDimension = isDimension
                            });
                            continue;
                        }
                        i++;
                    }
                    break;
                }
                i++;
            }

            // ── Pass 2: parse ENTITIES section ──
            i = 0;
            bool inEntities = false;
            while (i < pairs.Count)
            {
                var (code, value) = pairs[i];
                if (code == 0 && value == "SECTION" && i + 1 < pairs.Count &&
                    pairs[i + 1].code == 2 && pairs[i + 1].value == "ENTITIES")
                {
                    inEntities = true;
                    i += 2;
                    continue;
                }
                if (code == 0 && (value == "ENDSEC" || value == "EOF") && inEntities) break;
                if (!inEntities) { i++; continue; }

                if (code == 0)
                {
                    i = value switch
                    {
                        "LINE" => ParseLine(pairs, i + 1, model, layerNameToId),
                        "CIRCLE" => ParseCircle(pairs, i + 1, model, layerNameToId),
                        "ARC" => ParseArc(pairs, i + 1, model, layerNameToId),
                        "POINT" => ParsePoint(pairs, i + 1, model, layerNameToId),
                        "TEXT" or "MTEXT" => ParseText(pairs, i + 1, model, layerNameToId),
                        "LWPOLYLINE" => ParseLwPolyline(pairs, i + 1, model, layerNameToId),
                        "POLYLINE" => ParsePolyline(pairs, i + 1, model, layerNameToId),
                        "ELLIPSE" => ParseEllipse(pairs, i + 1, model, layerNameToId),
                        "SPLINE" => ParseSpline(pairs, i + 1, model, layerNameToId),
                        _ => i + 1
                    };
                }
                else
                {
                    i++;
                }
            }

            if (model.MinX == double.MaxValue)
            {
                model.MinX = model.MinY = 0;
                model.MaxX = model.MaxY = 1;
            }

            // Dimension color heuristic (same as TagParser)
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

            return model;
        }

        // ──────────────── Entity parsers ────────────────

        static int GetLayerId(string layerName, Dictionary<string, int> map) =>
            map.TryGetValue(layerName, out int id) ? id : 0;

        static int DetectLineType(string linetypeName) =>
            linetypeName != null &&
            (linetypeName.Contains("DASH", StringComparison.OrdinalIgnoreCase) ||
             linetypeName.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase))
                ? 1 : 0;

        static int ParseLine(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            string layer = "0"; int ci = -1; int lt = 0;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10: Prs(p[i].value, out x1); break;
                    case 20: Prs(p[i].value, out y1); break;
                    case 11: Prs(p[i].value, out x2); break;
                    case 21: Prs(p[i].value, out y2); break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            m.Entities.Add(new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2, LayerId = GetLayerId(layer, lm), ColorIndex = ci, LineType = lt });
            m.AddPoint(x1, y1); m.AddPoint(x2, y2);
            return i;
        }

        static int ParseCircle(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            double cx = 0, cy = 0, r = 0;
            string layer = "0"; int ci = -1; int lt = 0;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10: Prs(p[i].value, out cx); break;
                    case 20: Prs(p[i].value, out cy); break;
                    case 40: Prs(p[i].value, out r); break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            r = Math.Abs(r);
            m.Entities.Add(new TagCircle { CX = (float)cx, CY = (float)cy, R = (float)r, LayerId = GetLayerId(layer, lm), ColorIndex = ci, LineType = lt });
            m.AddPoint(cx - r, cy - r); m.AddPoint(cx + r, cy + r);
            return i;
        }

        static int ParseArc(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            double cx = 0, cy = 0, r = 0, sa = 0, ea = 360;
            string layer = "0"; int ci = -1; int lt = 0;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10: Prs(p[i].value, out cx); break;
                    case 20: Prs(p[i].value, out cy); break;
                    case 40: Prs(p[i].value, out r); break;
                    case 50: Prs(p[i].value, out sa); break;
                    case 51: Prs(p[i].value, out ea); break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            r = Math.Abs(r);
            double sweep = ((ea - sa) % 360.0 + 360.0) % 360.0;
            if (Math.Abs(sweep) < 1e-6) sweep = 360.0;
            m.Entities.Add(new TagArc { CX = (float)cx, CY = (float)cy, R = (float)r, StartAngle = (float)sa, SweepAngle = (float)sweep, LayerId = GetLayerId(layer, lm), ColorIndex = ci, LineType = lt });
            m.AddPoint(cx - r, cy - r); m.AddPoint(cx + r, cy + r);
            return i;
        }

        static int ParsePoint(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            double x = 0, y = 0;
            string layer = "0"; int ci = -1;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10: Prs(p[i].value, out x); break;
                    case 20: Prs(p[i].value, out y); break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                }
                i++;
            }
            m.Entities.Add(new TagPoint { X = (float)x, Y = (float)y, LayerId = GetLayerId(layer, lm), ColorIndex = ci });
            m.AddPoint(x, y);
            return i;
        }

        static int ParseText(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            double x = 0, y = 0, h = 10;
            string text = "";
            string layer = "0"; int ci = -1;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10: Prs(p[i].value, out x); break;
                    case 20: Prs(p[i].value, out y); break;
                    case 40: Prs(p[i].value, out h); break;
                    case 1: text += p[i].value; break;
                    case 3: text += p[i].value; break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                }
                i++;
            }
            text = text.Replace("\\P", "\n").Replace("\\p", "\n");
            text = Regex.Replace(text, @"\\[A-Za-z][^;\\]*;", "");
            text = text.Replace("{", "").Replace("}", "");
            text = Regex.Replace(text, @"\\[OoLlKk~]", "");
            text = text.Replace("%%d", "\u00b0").Replace("%%D", "\u00b0")
                       .Replace("%%p", "\u00b1").Replace("%%P", "\u00b1")
                       .Replace("%%c", "\u2205").Replace("%%C", "\u2205")
                       .Replace("%%%", "%");
            text = text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                m.Entities.Add(new TagText { X = (float)x, Y = (float)y, Text = text, Size = (float)h, LayerId = GetLayerId(layer, lm), ColorIndex = ci });
                m.AddPoint(x, y);
            }
            return i;
        }

        static int ParseLwPolyline(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            var verts = new List<(double x, double y)>();
            bool closed = false;
            string layer = "0"; int ci = -1; int lt = 0;
            double cx = 0, cy = 0; bool hasX = false;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10:
                        if (hasX) verts.Add((cx, cy));
                        Prs(p[i].value, out cx); hasX = true; break;
                    case 20: Prs(p[i].value, out cy); break;
                    case 70: if (int.TryParse(p[i].value, out int fl)) closed = (fl & 1) != 0; break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            if (hasX) verts.Add((cx, cy));
            int lid = GetLayerId(layer, lm);
            for (int j = 0; j < verts.Count - 1; j++)
            {
                m.Entities.Add(new TagLine { X1 = (float)verts[j].x, Y1 = (float)verts[j].y, X2 = (float)verts[j + 1].x, Y2 = (float)verts[j + 1].y, LayerId = lid, ColorIndex = ci, LineType = lt });
                m.AddPoint(verts[j].x, verts[j].y);
            }
            if (closed && verts.Count > 1)
                m.Entities.Add(new TagLine { X1 = (float)verts[^1].x, Y1 = (float)verts[^1].y, X2 = (float)verts[0].x, Y2 = (float)verts[0].y, LayerId = lid, ColorIndex = ci, LineType = lt });
            if (verts.Count > 0) m.AddPoint(verts[^1].x, verts[^1].y);
            return i;
        }

        static int ParsePolyline(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            string layer = "0"; int ci = -1; int lt = 0; bool closed = false;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 70: if (int.TryParse(p[i].value, out int fl)) closed = (fl & 1) != 0; break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            var verts = new List<(double x, double y)>();
            while (i < p.Count)
            {
                if (p[i].code == 0)
                {
                    if (p[i].value == "SEQEND") { i++; break; }
                    if (p[i].value == "VERTEX")
                    {
                        i++;
                        double vx = 0, vy = 0;
                        while (i < p.Count && p[i].code != 0)
                        {
                            if (p[i].code == 10) Prs(p[i].value, out vx);
                            else if (p[i].code == 20) Prs(p[i].value, out vy);
                            i++;
                        }
                        verts.Add((vx, vy));
                        continue;
                    }
                    break;
                }
                i++;
            }
            int lid = GetLayerId(layer, lm);
            for (int j = 0; j < verts.Count - 1; j++)
            {
                m.Entities.Add(new TagLine { X1 = (float)verts[j].x, Y1 = (float)verts[j].y, X2 = (float)verts[j + 1].x, Y2 = (float)verts[j + 1].y, LayerId = lid, ColorIndex = ci, LineType = lt });
                m.AddPoint(verts[j].x, verts[j].y);
            }
            if (closed && verts.Count > 1)
                m.Entities.Add(new TagLine { X1 = (float)verts[^1].x, Y1 = (float)verts[^1].y, X2 = (float)verts[0].x, Y2 = (float)verts[0].y, LayerId = lid, ColorIndex = ci, LineType = lt });
            if (verts.Count > 0) m.AddPoint(verts[^1].x, verts[^1].y);
            return i;
        }

        static int ParseEllipse(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            double cx = 0, cy = 0, majX = 0, majY = 0, ratio = 1, sp = 0, ep = 2 * Math.PI;
            string layer = "0"; int ci = -1; int lt = 0;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10: Prs(p[i].value, out cx); break;
                    case 20: Prs(p[i].value, out cy); break;
                    case 11: Prs(p[i].value, out majX); break;
                    case 21: Prs(p[i].value, out majY); break;
                    case 40: Prs(p[i].value, out ratio); break;
                    case 41: Prs(p[i].value, out sp); break;
                    case 42: Prs(p[i].value, out ep); break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            double a = Math.Sqrt(majX * majX + majY * majY);
            double b = a * ratio;
            double rot = Math.Atan2(majY, majX);
            int lid = GetLayerId(layer, lm);
            int segs = 64;
            double range = ep - sp; if (range < 0) range += 2 * Math.PI;
            double step = range / segs;
            double px = 0, py = 0;
            for (int j = 0; j <= segs; j++)
            {
                double t = sp + step * j;
                double lx = a * Math.Cos(t), ly = b * Math.Sin(t);
                double rx = cx + lx * Math.Cos(rot) - ly * Math.Sin(rot);
                double ry = cy + lx * Math.Sin(rot) + ly * Math.Cos(rot);
                if (j > 0)
                    m.Entities.Add(new TagLine { X1 = (float)px, Y1 = (float)py, X2 = (float)rx, Y2 = (float)ry, LayerId = lid, ColorIndex = ci, LineType = lt });
                m.AddPoint(rx, ry);
                px = rx; py = ry;
            }
            return i;
        }

        static int ParseSpline(List<(int code, string value)> p, int i, TagModel m, Dictionary<string, int> lm)
        {
            var ctlPts = new List<(double x, double y)>();
            var fitPts = new List<(double x, double y)>();
            string layer = "0"; int ci = -1; int lt = 0;
            double cx = 0, cy = 0, fx = 0, fy = 0;
            bool hasCtl = false, hasFit = false;
            while (i < p.Count && p[i].code != 0)
            {
                switch (p[i].code)
                {
                    case 8: layer = p[i].value; break;
                    case 10:
                        if (hasCtl) ctlPts.Add((cx, cy));
                        Prs(p[i].value, out cx); hasCtl = true; break;
                    case 20: Prs(p[i].value, out cy); break;
                    case 11:
                        if (hasFit) fitPts.Add((fx, fy));
                        Prs(p[i].value, out fx); hasFit = true; break;
                    case 21: Prs(p[i].value, out fy); break;
                    case 62: int.TryParse(p[i].value, out ci); break;
                    case 6: lt = DetectLineType(p[i].value); break;
                }
                i++;
            }
            if (hasCtl) ctlPts.Add((cx, cy));
            if (hasFit) fitPts.Add((fx, fy));
            var pts = fitPts.Count > 0 ? fitPts : ctlPts;
            int lid = GetLayerId(layer, lm);
            for (int j = 0; j < pts.Count - 1; j++)
            {
                m.Entities.Add(new TagLine { X1 = (float)pts[j].x, Y1 = (float)pts[j].y, X2 = (float)pts[j + 1].x, Y2 = (float)pts[j + 1].y, LayerId = lid, ColorIndex = ci, LineType = lt });
                m.AddPoint(pts[j].x, pts[j].y);
            }
            if (pts.Count > 0) m.AddPoint(pts[^1].x, pts[^1].y);
            return i;
        }

        // ──────────────── I/O helpers ────────────────

        static List<(int code, string value)> ReadPairs(Stream stream)
        {
            byte[] bytes;
            using (var ms = new MemoryStream()) { stream.CopyTo(ms); bytes = ms.ToArray(); }

            string content;
            try
            {
                using var ms2 = new MemoryStream(bytes);
                using var tr = new StreamReader(ms2, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                content = tr.ReadToEnd();
                if (content.IndexOf('\uFFFD') >= 0)
                    content = Encoding.Latin1.GetString(bytes);
            }
            catch { content = Encoding.Latin1.GetString(bytes); }

            var lines = content.Split('\n');
            var pairs = new List<(int code, string value)>(lines.Length / 2);
            int idx = 0;
            while (idx + 1 < lines.Length)
            {
                var codeLine = lines[idx].Trim();
                var valueLine = lines[idx + 1].Trim();
                idx += 2;
                if (int.TryParse(codeLine, out int code))
                    pairs.Add((code, valueLine));
            }
            return pairs;
        }

        static bool Prs(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
