using System;
using System.Text;
using System.Globalization;

namespace test
{
    public static class SvgExporter
    {
        public static string ExportToSvg(TagModel model)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            double w = model.MaxX - model.MinX;
            double h = model.MaxY - model.MinY;
            if (w <= 0) w = 100;
            if (h <= 0) h = 100;
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{model.MinX.ToString(CultureInfo.InvariantCulture)} {model.MinY.ToString(CultureInfo.InvariantCulture)} {w.ToString(CultureInfo.InvariantCulture)} {h.ToString(CultureInfo.InvariantCulture)}\">\n");

            foreach (var ent in model.Entities)
            {
                string stroke = "black";
                string fill = "none";
                try
                {
                    var lid = (int)ent.GetType().GetProperty("LayerId")?.GetValue(ent)!;
                    var lay = model.Layers.Find(x => x.Id == lid);
                    if (lay != null)
                    {
                        var c = lay.Color;
                        // if nearly white, use black for visibility
                        if (c.Red >= 0.95 && c.Green >= 0.95 && c.Blue >= 0.95)
                            stroke = "black";
                        else
                            stroke = $"rgb({(int)(c.Red*255)},{(int)(c.Green*255)},{(int)(c.Blue*255)})";
                    }
                }
                catch { }

                switch (ent)
                {
                    case TagLine l:
                        sb.AppendLine($"<line x1=\"{l.X1.ToString(CultureInfo.InvariantCulture)}\" y1=\"{l.Y1.ToString(CultureInfo.InvariantCulture)}\" x2=\"{l.X2.ToString(CultureInfo.InvariantCulture)}\" y2=\"{l.Y2.ToString(CultureInfo.InvariantCulture)}\" stroke=\"black\" stroke-width=\"1\" />");
                        break;
                    case TagCircle c:
                        sb.AppendLine($"<circle cx=\"{c.CX.ToString(CultureInfo.InvariantCulture)}\" cy=\"{c.CY.ToString(CultureInfo.InvariantCulture)}\" r=\"{c.R.ToString(CultureInfo.InvariantCulture)}\" stroke=\"black\" fill=\"{fill}\" />");
                        break;
                    case TagArc a:
                        // approximate arc as a polyline/path and render in black
                        try
                        {
                            double start = a.StartAngle;
                            double sweep = a.SweepAngle;
                            double startRad = start * Math.PI / 180.0;
                            double sweepRad = ((sweep % 360.0) + 360.0) % 360.0 * Math.PI / 180.0;
                            if (Math.Abs(sweepRad) < 1e-9) sweepRad = 2 * Math.PI;
                            int segs = Math.Max(6, (int)Math.Ceiling(Math.Abs(sweepRad) / (2 * Math.PI) * 128));
                            double step = sweepRad / segs;
                            var pts = new System.Text.StringBuilder();
                            for (int i = 0; i <= segs; i++)
                            {
                                double ang = startRad + step * i;
                                double px = a.CX + a.R * Math.Cos(ang);
                                double py = a.CY + a.R * Math.Sin(ang);
                                if (i == 0)
                                    pts.AppendFormat(CultureInfo.InvariantCulture, "M {0} {1}", px, py);
                                else
                                    pts.AppendFormat(CultureInfo.InvariantCulture, " L {0} {1}", px, py);
                            }
                            sb.AppendLine($"<path d=\"{pts}\" stroke=\"black\" fill=\"none\" stroke-width=\"1\" />");
                        }
                        catch
                        {
                            // fallback: render as a small circle at center
                            sb.AppendLine($"<circle cx=\"{a.CX.ToString(CultureInfo.InvariantCulture)}\" cy=\"{a.CY.ToString(CultureInfo.InvariantCulture)}\" r=\"1\" stroke=\"black\" fill=\"black\" />");
                        }
                        break;
                    case TagPoint p:
                        // render a small filled circle for point
                        sb.AppendLine($"<circle cx=\"{p.X.ToString(CultureInfo.InvariantCulture)}\" cy=\"{p.Y.ToString(CultureInfo.InvariantCulture)}\" r=\"1\" stroke=\"black\" fill=\"black\" />");
                        break;
                    case TagText t:
                        sb.AppendLine($"<text x=\"{t.X.ToString(CultureInfo.InvariantCulture)}\" y=\"{t.Y.ToString(CultureInfo.InvariantCulture)}\" font-size=\"{t.Size.ToString(CultureInfo.InvariantCulture)}\" fill=\"black\">{System.Security.SecurityElement.Escape(t.Text)}</text>");
                        break;
                }
            }

            sb.AppendLine("</svg>");
            return sb.ToString();
        }
    }
}
