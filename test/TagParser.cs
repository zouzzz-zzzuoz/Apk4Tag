using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Maui.Graphics;

namespace test
{
    // Very small .tag parser that extracts lines and basic primitives (2: lines, 3: arcs/ellipses simplified, 4: points)
    public class TagModel
    {
        public List<ITagEntity> Entities { get; } = new List<ITagEntity>();
        public List<TagLayer> Layers { get; } = new List<TagLayer>();
        public HashSet<int> DimensionColorIndices { get; } = new HashSet<int>();
        public double MinX = double.MaxValue;
        public double MinY = double.MaxValue;
        public double MaxX = double.MinValue;
        public double MaxY = double.MinValue;

        private Dictionary<int, TagLayer>? _layerDict;
        public Dictionary<int, TagLayer> LayerDict => _layerDict ??= BuildLayerDict();
        private Dictionary<int, TagLayer> BuildLayerDict()
        {
            var d = new Dictionary<int, TagLayer>(Layers.Count);
            foreach (var l in Layers)
                d[l.Id] = l;
            return d;
        }
        public void InvalidateLayerCache() => _layerDict = null;

        public void AddPoint(double x, double y)
        {
            MinX = Math.Min(MinX, x);
            MinY = Math.Min(MinY, y);
            MaxX = Math.Max(MaxX, x);
            MaxY = Math.Max(MaxY, y);
        }
    }

    public interface ITagEntity { int LayerId { get; set; } int LineType { get; set; } int ColorIndex { get; set; } }

    public class TagLayer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "Default";
        public bool Visible { get; set; } = true;
        public Color Color { get; set; } = Colors.Black;
        public int LineType { get; set; } = 0; // 0=continuous, >0=dashed/hidden
        public bool IsDimension { get; set; } = false; // true for cotation/dimension layers
    }

    public class TagLine : ITagEntity
    {
        public float X1, Y1, X2, Y2;
        public int LayerId { get; set; }
        public int LineType { get; set; }
        public int ColorIndex { get; set; }
    }

    public class TagPoint : ITagEntity
    {
        public float X, Y;
        public int LayerId { get; set; }
        public int LineType { get; set; }
        public int ColorIndex { get; set; }
    }

    public class TagCircle : ITagEntity
    {
        public float CX, CY, R;
        public int LayerId { get; set; }
        public int LineType { get; set; }
        public int ColorIndex { get; set; }
    }

    public class TagArc : ITagEntity
    {
        public float CX, CY, R, StartAngle, SweepAngle;
        public int LayerId { get; set; }
        public int LineType { get; set; }
        public int ColorIndex { get; set; }
    }

    public class TagText : ITagEntity
    {
        public float X, Y;
        public string Text = "";
        public float Size = 10;
        public int LayerId { get; set; }
        public int LineType { get; set; }
        public int ColorIndex { get; set; }
    }

    public static class TagParser
    {
        public static TagModel Parse(Stream stream)
        {
            // read raw bytes then try to decode text robustly (UTF-8 with BOM, else fallback to windows-1252)
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            string content;
            try
            {
                using (var ms2 = new MemoryStream(bytes))
                using (var tr = new StreamReader(ms2, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    content = tr.ReadToEnd();
                }
                // if replacement character found, likely wrong decoding -> fallback to windows-1252
                if (content.IndexOf('\uFFFD') >= 0 || content.Contains('\uFFFD'))
                {
                    // fallback to Latin1 which covers common Western ANSI characters (é, Ø, etc.)
                    content = Encoding.Latin1.GetString(bytes);
                }
            }
            catch
            {
                content = Encoding.Latin1.GetString(bytes);
            }

            // Read all lines for two-pass processing (layers must be known before entity parsing)
            var allLines = new List<string>();
            using (var sr = new StringReader(content))
            {
                string? rdLine;
                while ((rdLine = sr.ReadLine()) != null)
                    allLines.Add(rdLine);
            }

            var model = new TagModel();
            // ensure at least one default layer
            model.Layers.Add(new TagLayer { Id = 0, Name = "Default", Color = Colors.Black, Visible = true });

            // Pass 1: parse layer definitions (code 200) so all layers are known before entity parsing
            foreach (var line in allLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int c200) || c200 != 200) continue;
                if (int.TryParse(parts[1], out int lid))
                {
                    var layer = new TagLayer { Id = lid };
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int lt) && lt >= 0 && lt <= 10)
                        layer.LineType = lt;
                    if (parts.Length >= 7)
                    {
                        if (int.TryParse(parts[5], out int col))
                        {
                            var redByte = (byte)((col >> 16) & 0xFF);
                            var greenByte = (byte)((col >> 8) & 0xFF);
                            var blueByte = (byte)(col & 0xFF);
                            layer.Color = new Microsoft.Maui.Graphics.Color((float)redByte / 255f, (float)greenByte / 255f, (float)blueByte / 255f);
                        }
                        layer.Name = parts[6];
                    }
                    if (!string.IsNullOrEmpty(layer.Name))
                    {
                        var nameLower = layer.Name.ToLowerInvariant();
                        if (layer.LineType == 0)
                        {
                            if (nameLower.Contains("cach") || nameLower.Contains("hidden") ||
                                nameLower.Contains("interrompu") || nameLower.Contains("tiret") ||
                                nameLower.Contains("dash"))
                                layer.LineType = 1;
                        }
                        // detect dimension/cotation layers
                        if (nameLower.Contains("cot") || nameLower.Contains("dimension") || nameLower.Contains("dim"))
                            layer.IsDimension = true;
                    }
                    model.Layers.Add(layer);
                }
            }

            // Pass 2: parse entities (all layers are now known)
            for (int li = 0; li < allLines.Count; li++)
            {
                var line = allLines[li];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var parts = line.Split(',');
                if (parts.Length == 0)
                    continue;
                if (!int.TryParse(parts[0], out int code))
                    continue;

                switch (code)
                {
                    case 200: // already processed in pass 1
                        break;
                    case 16: // text block: 16,x,y,... then following lines until a line with single '0'
                        if (parts.Length >= 3 && TryParse(parts[1], out double tx) && TryParse(parts[2], out double ty))
                        {
                            float tsize = 10f;
                            if (parts.Length >= 6 && TryParse(parts[5], out double ts))
                                tsize = (float)ts;

                            var sb = new System.Text.StringBuilder();
                            while (li + 1 < allLines.Count)
                            {
                                li++;
                                var txtLine = allLines[li];
                                if (txtLine == "0")
                                    break;
                                var trimmed = txtLine.TrimStart();
                                if (trimmed.Length > 0 && char.IsDigit(trimmed[0]))
                                {
                                    var commaIdx = trimmed.IndexOf(',');
                                    if (commaIdx > 0 && trimmed.Substring(0, commaIdx).All(char.IsDigit))
                                    {
                                        // This line looks like a new entity code (e.g. "99,...", "98,...", "2,...", "4,...").
                                        // Stop reading text and back up so the outer loop processes this line.
                                        li--;
                                        break;
                                    }
                                }
                                if (sb.Length > 0)
                                    sb.Append("\n");
                                sb.Append(txtLine);
                            }

                            var raw = sb.ToString();
                            if (raw.StartsWith("\\f"))
                            {
                                int idx = raw.IndexOf(';');
                                if (idx >= 0 && idx + 1 < raw.Length)
                                    raw = raw.Substring(idx + 1);
                            }
                            raw = raw.Replace("\\P", "\n").Replace("\\p", "\n");

                            // Strip MTEXT formatting codes (\Xvalue; patterns like \I15; \W1; \H2.5; \C1; \T1; \Q15; \A1; \S...;)
                            raw = Regex.Replace(raw, @"\\[A-Za-z][^;\\]*;", "");
                            // Remove grouping braces used in MTEXT
                            raw = raw.Replace("{", "").Replace("}", "");
                            // Remove remaining backslash formatting toggles (\O \o \L \l \K \k \~ etc.)
                            raw = Regex.Replace(raw, @"\\[OoLlKk~]", "");
                            // Convert literal \uXXXX and \U+XXXX Unicode escape sequences to actual characters
                            raw = Regex.Replace(raw, @"\\[uU]\+?([0-9A-Fa-f]{4})", m =>
                                ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
                            // Convert AutoCAD %%X special character codes
                            raw = raw.Replace("%%d", "\u00b0")  // ° degree
                                     .Replace("%%D", "\u00b0")
                                     .Replace("%%p", "\u00b1")  // ± plus-minus
                                     .Replace("%%P", "\u00b1")
                                     .Replace("%%c", "\u2205")  // ∅ diameter
                                     .Replace("%%C", "\u2205")
                                     .Replace("%%%", "%");
                            raw = raw.Trim();

                            var t = new TagText { X = (float)tx, Y = (float)ty, Text = raw, Size = tsize };
                            var (tLid, tLt, tCi) = ParseTrailing(parts, 3, model);
                            t.LayerId = tLid;
                            t.LineType = tLt;
                            t.ColorIndex = tCi;
                            model.Entities.Add(t);
                            model.AddPoint(tx, ty);
                        }
                        break;
                    case 5: // circle: 5,cx,cy,r
                        if (parts.Length >= 4 && TryParse(parts[1], out double ccx) && TryParse(parts[2], out double ccy) && TryParse(parts[3], out double cr))
                        {
                            var radius = Math.Abs(cr);
                            var circ = new TagCircle { CX = (float)ccx, CY = (float)ccy, R = (float)radius };
                            var (c5Lid, c5Lt, c5Ci) = ParseTrailing(parts, 4, model);
                            circ.LayerId = c5Lid;
                            circ.LineType = c5Lt;
                            circ.ColorIndex = c5Ci;
                            model.Entities.Add(circ);
                            model.AddPoint(ccx - radius, ccy - radius);
                            model.AddPoint(ccx + radius, ccy + radius);
                        }
                        break;
                    case 2: // line: 2,x1,y1,x2,y2,...
                        if (parts.Length >= 5 && TryParse(parts[1], out double x1) && TryParse(parts[2], out double y1) && TryParse(parts[3], out double x2) && TryParse(parts[4], out double y2))
                        {
                            var lineEnt = new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2 };
                            var (l2Lid, l2Lt, l2Ci) = ParseTrailing(parts, 5, model);
                            lineEnt.LayerId = l2Lid;
                            lineEnt.LineType = l2Lt;
                            lineEnt.ColorIndex = l2Ci;
                            model.Entities.Add(lineEnt);
                            model.AddPoint(x1, y1);
                            model.AddPoint(x2, y2);
                        }
                        break;
                    case 3: // arc / circle
                        if (parts.Length >= 6 && TryParse(parts[1], out double cx) && TryParse(parts[2], out double cy) && TryParse(parts[3], out double rRaw) && TryParse(parts[4], out double start) && TryParse(parts[5], out double end))
                        {
                            double r = Math.Abs(rRaw);
                            double raw = end - start;
                            double sweep = ((raw % 360.0) + 360.0) % 360.0;
                            if (Math.Abs(sweep) < 1e-6) sweep = 360.0;
                            var arc = new TagArc { CX = (float)cx, CY = (float)cy, R = (float)r, StartAngle = (float)start, SweepAngle = (float)sweep };
                            var (a3Lid, a3Lt, a3Ci) = ParseTrailing(parts, 6, model);
                            arc.LayerId = a3Lid;
                            arc.LineType = a3Lt;
                            arc.ColorIndex = a3Ci;
                            model.Entities.Add(arc);
                            model.AddPoint(cx - r, cy - r);
                            model.AddPoint(cx + r, cy + r);
                        }
                        else if (parts.Length >= 3 && TryParse(parts[1], out double ax) && TryParse(parts[2], out double ay))
                        {
                            var p = new TagPoint { X = (float)ax, Y = (float)ay };
                            var (p3Lid, p3Lt, p3Ci) = ParseTrailing(parts, 3, model);
                            p.LayerId = p3Lid;
                            p.LineType = p3Lt;
                            p.ColorIndex = p3Ci;
                            model.Entities.Add(p);
                            model.AddPoint(ax, ay);
                        }
                        break;
                    case 4: // vertex/point or circle: 4,x,y,radius,...
                        if (parts.Length >= 4 && TryParse(parts[1], out double px) && TryParse(parts[2], out double py) && TryParse(parts[3], out double rad))
                        {
                            double absRad = Math.Abs(rad);
                            if (absRad > 0.0001)
                            {
                                var circ = new TagCircle { CX = (float)px, CY = (float)py, R = (float)absRad };
                                var (c4Lid, c4Lt, c4Ci) = ParseTrailing(parts, 4, model);
                                circ.LayerId = c4Lid;
                                circ.LineType = c4Lt;
                                circ.ColorIndex = c4Ci;
                                model.Entities.Add(circ);
                                model.AddPoint(px - absRad, py - absRad);
                                model.AddPoint(px + absRad, py + absRad);
                            }
                            else
                            {
                                var p2 = new TagPoint { X = (float)px, Y = (float)py };
                                var (p4aLid, p4aLt, p4aCi) = ParseTrailing(parts, 3, model);
                                p2.LayerId = p4aLid;
                                p2.LineType = p4aLt;
                                p2.ColorIndex = p4aCi;
                                model.Entities.Add(p2);
                                model.AddPoint(px, py);
                            }
                        }
                        else if (parts.Length >= 3 && TryParse(parts[1], out px) && TryParse(parts[2], out py))
                        {
                            var p2 = new TagPoint { X = (float)px, Y = (float)py };
                            var (p4bLid, p4bLt, p4bCi) = ParseTrailing(parts, 3, model);
                            p2.LayerId = p4bLid;
                            p2.LineType = p4bLt;
                            p2.ColorIndex = p4bCi;
                            model.Entities.Add(p2);
                            model.AddPoint(px, py);
                        }
                        break;
                }
            }

            if (model.MinX == double.MaxValue)
            {
                model.MinX = model.MinY = 0;
                model.MaxX = model.MaxY = 1;
            }

            // Compute dimension color indices using majority-color heuristic:
            // In a CAD drawing, geometry entities (outlines) vastly outnumber dimension entities.
            // The most common color index among continuous-line entities is the geometry color.
            // All other color indices are likely dimension/annotation colors.
            var colorCounts = new Dictionary<int, int>();
            int textEntityCount = 0;
            foreach (var ent in model.Entities)
            {
                if (ent is TagText) { textEntityCount++; continue; }
                if (ent.ColorIndex < 0) continue;
                colorCounts.TryGetValue(ent.ColorIndex, out int cnt);
                colorCounts[ent.ColorIndex] = cnt + 1;
            }
            int majorityColor = -1;
            int maxCount = 0;
            foreach (var kv in colorCounts)
            {
                if (kv.Value > maxCount) { maxCount = kv.Value; majorityColor = kv.Key; }
            }
            // Every non-majority color is a dimension/annotation color
            foreach (var kv in colorCounts)
            {
                if (kv.Key != majorityColor)
                    model.DimensionColorIndices.Add(kv.Key);
            }

            // Filtrer les annotations de cartouche / notes techniques indésirables
            var filteredTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dimensions",
                "poids",
                "désignation",
                "21.4",
                "Gousset",
                "Ecrou",
                "Tôle",
                "BL",
                "Gousset ép8 mm",
            };
            var filteredContains = new string[]
            {
                "meuler les soudures",
                "revêtement",
                "sauf annotation",
                "tole ép.",
                "tôle ép.",
                "perçage",
                "poids total",
                "Gousset ép8 mm",
                "Ecrou",
                "Tôle",
                "BL",
            };
            model.Entities.RemoveAll(e =>
            {
                if (e is not TagText txt) return false;
                var text = txt.Text.Trim();
                if (string.IsNullOrEmpty(text)) return true;
                if (filteredTexts.Contains(text)) return true;
                var lower = text.ToLowerInvariant();
                foreach (var pattern in filteredContains)
                {
                    if (lower.Contains(pattern))
                        return true;
                }
                return false;
            });

            // Dédupliquer les textes identiques à des positions très proches
            // (artefact courant de la conversion DWG → TAG)
            var textEntities = model.Entities.OfType<TagText>().ToList();
            var textDuplicates = new HashSet<TagText>();
            for (int i = 0; i < textEntities.Count; i++)
            {
                if (textDuplicates.Contains(textEntities[i])) continue;
                for (int j = i + 1; j < textEntities.Count; j++)
                {
                    if (textDuplicates.Contains(textEntities[j])) continue;
                    var a = textEntities[i];
                    var b = textEntities[j];
                    if (string.Equals(a.Text.Trim(), b.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        float dx = a.X - b.X;
                        float dy = a.Y - b.Y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        // si même texte à moins de 2× la taille de police, c'est un doublon
                        if (dist < a.Size * 2f)
                            textDuplicates.Add(b);
                    }
                }
            }
            if (textDuplicates.Count > 0)
                model.Entities.RemoveAll(e => textDuplicates.Contains(e));

            return model;
        }

        /// <summary>
        /// Lightweight parse for mini-preview: geometry only (no text, no dimension detection, no post-processing).
        /// </summary>
        public static TagModel ParseLightweight(Stream stream)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            // For geometry-only parsing, Latin1 is sufficient (numeric values decode correctly)
            var content = Encoding.Latin1.GetString(bytes);

            var model = new TagModel();
            model.Layers.Add(new TagLayer { Id = 0, Name = "Default", Color = Colors.Black, Visible = true });

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                var parts = line.Split(',');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int code)) continue;

                switch (code)
                {
                    case 16: // text block (lightweight): take inline text if present
                        if (parts.Length >= 3 && TryParse(parts[1], out double tx) && TryParse(parts[2], out double ty))
                        {
                            float tsize = 10f;
                            if (parts.Length >= 6 && TryParse(parts[5], out double ts))
                                tsize = (float)ts;

                            string raw = string.Empty;
                            if (parts.Length >= 4)
                            {
                                // join remaining fields as a best-effort short text preview
                                raw = string.Join(",", parts.Skip(3)).Trim();
                                raw = raw.Replace("\\P", "\n").Replace("\\p", "\n");
                            }
                            var t = new TagText { X = (float)tx, Y = (float)ty, Text = raw, Size = tsize };
                            model.Entities.Add(t);
                            model.AddPoint(tx, ty);
                        }
                        break;
                    case 2:
                        if (parts.Length >= 5 && TryParse(parts[1], out double x1) && TryParse(parts[2], out double y1) && TryParse(parts[3], out double x2) && TryParse(parts[4], out double y2))
                        {
                            model.Entities.Add(new TagLine { X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2 });
                            model.AddPoint(x1, y1);
                            model.AddPoint(x2, y2);
                        }
                        break;
                    case 3:
                        if (parts.Length >= 6 && TryParse(parts[1], out double cx) && TryParse(parts[2], out double cy) && TryParse(parts[3], out double rRaw) && TryParse(parts[4], out double start) && TryParse(parts[5], out double end))
                        {
                            double r = Math.Abs(rRaw);
                            double sweep = ((end - start) % 360.0 + 360.0) % 360.0;
                            if (Math.Abs(sweep) < 1e-6) sweep = 360.0;
                            model.Entities.Add(new TagArc { CX = (float)cx, CY = (float)cy, R = (float)r, StartAngle = (float)start, SweepAngle = (float)sweep });
                            model.AddPoint(cx - r, cy - r);
                            model.AddPoint(cx + r, cy + r);
                        }
                        break;
                    case 4:
                        if (parts.Length >= 4 && TryParse(parts[1], out double px) && TryParse(parts[2], out double py) && TryParse(parts[3], out double diam))
                        {
                            double absDiam = Math.Abs(diam);
                            if (absDiam > 0.0001)
                            {
                                model.Entities.Add(new TagCircle { CX = (float)px, CY = (float)py, R = (float)(absDiam / 2.0) });
                                model.AddPoint(px - absDiam / 2.0, py - absDiam / 2.0);
                                model.AddPoint(px + absDiam / 2.0, py + absDiam / 2.0);
                            }
                        }
                        else if (parts.Length >= 3 && TryParse(parts[1], out px) && TryParse(parts[2], out py))
                        {
                            model.AddPoint(px, py);
                        }
                        break;
                    case 5:
                        if (parts.Length >= 4 && TryParse(parts[1], out double ccx) && TryParse(parts[2], out double ccy) && TryParse(parts[3], out double cr))
                        {
                            double radius = Math.Abs(cr);
                            model.Entities.Add(new TagCircle { CX = (float)ccx, CY = (float)ccy, R = (float)radius });
                            model.AddPoint(ccx - radius, ccy - radius);
                            model.AddPoint(ccx + radius, ccy + radius);
                        }
                        break;
                }
            }

            if (model.MinX == double.MaxValue)
            {
                model.MinX = model.MinY = 0;
                model.MaxX = model.MaxY = 1;
            }

            return model;
        }

        static bool TryParse(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
        /// Parse trailing fields after geometry: [color, pen, weight?, layer]
        /// - Color index: first trailing field (startIndex)
        /// - Layer ID: last trailing integer matching a known layer (scanned end→start)
        /// - Line type: second trailing field (pen): 1=continuous, ≥2=dashed/hidden
        /// </summary>
        static (int layerId, int lineType, int colorIndex) ParseTrailing(string[] parts, int startIndex, TagModel model)
        {
            int layerId = model.Layers.Count > 0 ? model.Layers[0].Id : 0;
            int lineType = 0;
            int colorIndex = -1;

            // Color index: first trailing field
            if (startIndex < parts.Length && int.TryParse(parts[startIndex], out int ci))
                colorIndex = ci;

            // Layer: last trailing integer matching a known layer
            for (int i = parts.Length - 1; i >= startIndex; i--)
            {
                if (int.TryParse(parts[i], out int cand))
                {
                    foreach (var L in model.Layers)
                        if (L.Id == cand)
                        {
                            layerId = cand;
                            goto layerDone;
                        }
                }
            }
            layerDone:

            // Pen/lineType: second trailing field (startIndex + 1)
            // In .tag format trailing is [color, pen, weight?, layer]
            // pen=1 → continuous, pen≥2 → dashed/hidden
            if (startIndex + 1 < parts.Length && int.TryParse(parts[startIndex + 1], out int pen) && pen >= 2 && pen <= 10)
                lineType = pen;

            return (layerId, lineType, colorIndex);
        }
    }
}
