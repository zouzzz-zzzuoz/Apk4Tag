using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Text;
using ExcelDataReader;

namespace test
{
    internal static class ExcelParser
    {
        /// <summary>
        /// Lit un fichier XLS ou XLSX et retourne le contenu sous forme de HTML.
        /// </summary>
        internal static string ToHtml(string filePath)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration
                {
                    UseHeaderRow = false
                }
            });

            // Extraire les images intégrées
            var images = ExtractImages(filePath);

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
            sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'/>");
            sb.AppendLine("<style>");
            sb.AppendLine("* { box-sizing: border-box; }");
            sb.AppendLine("body { font-family: sans-serif; font-size: 14px; margin: 0; padding: 4px; }");
            sb.AppendLine("h3 { margin: 12px 0 6px 0; }");
            sb.AppendLine(".table-wrap { overflow-x: auto; -webkit-overflow-scrolling: touch; margin-bottom: 16px; }");
            sb.AppendLine("table { border-collapse: collapse; min-width: 100%; }");
            sb.AppendLine("th, td { border: 1px solid #999; padding: 5px 8px; text-align: left; word-break: break-word; max-width: 300px; }");
            sb.AppendLine("th { background-color: #4a4a8a; color: white; position: sticky; top: 0; z-index: 1; }");
            sb.AppendLine("tr:nth-child(even) { background-color: #f2f2f2; }");
            sb.AppendLine(".images-section { margin-top: 20px; padding: 8px; background: #fafafa; border-top: 2px solid #4a4a8a; }");
            sb.AppendLine(".images-section h3 { color: #4a4a8a; }");
            sb.AppendLine(".excel-image { width: 100%; height: auto; margin: 10px 0; display: block; border: 1px solid #ccc; border-radius: 4px; background: white; }");
                        sb.AppendLine("</style></head><body>");

            for (int t = 0; t < dataSet.Tables.Count; t++)
            {
                var table = dataSet.Tables[t];
                if (table.Rows.Count == 0 && table.Columns.Count == 0) continue;

                sb.AppendLine($"<h3>{HtmlEncode(table.TableName)}</h3>");
                sb.AppendLine("<div class='table-wrap'><table>");

                // Utiliser la première ligne comme en-tête visuel
                int dataStartRow = 0;
                if (table.Rows.Count > 0)
                {
                    sb.Append("<tr>");
                    for (int c = 0; c < table.Columns.Count; c++)
                    {
                        var val = table.Rows[0][c];
                        string text = val == null || val == DBNull.Value ? "" : val.ToString() ?? "";
                        sb.Append($"<th>{HtmlEncode(text)}</th>");
                    }
                    sb.AppendLine("</tr>");
                    dataStartRow = 1;
                }

                // Lignes de données (limiter à 5000 lignes pour la performance)
                int maxRows = Math.Min(table.Rows.Count, 5000);
                for (int r = dataStartRow; r < maxRows; r++)
                {
                    sb.Append("<tr>");
                    for (int c = 0; c < table.Columns.Count; c++)
                    {
                        var val = table.Rows[r][c];
                        string text = val == null || val == DBNull.Value ? "" : val.ToString() ?? "";
                        sb.Append($"<td>{HtmlEncode(text)}</td>");
                    }
                    sb.AppendLine("</tr>");
                }

                if (table.Rows.Count > maxRows)
                {
                    sb.AppendLine($"<tr><td colspan='{table.Columns.Count}' style='text-align:center;color:gray;'>… {table.Rows.Count - maxRows} lignes supplémentaires non affichées …</td></tr>");
                }

                sb.AppendLine("</table></div>");
            }

            // Ajouter les images extraites dans une section dédiée
            if (images.Count > 0)
            {
                sb.AppendLine("<div class='images-section'>");
                sb.AppendLine($"<h3>Images intégrées ({images.Count})</h3>");
                for (int i = 0; i < images.Count; i++)
                {
                    var (mimeType, data) = images[i];
                    string base64 = Convert.ToBase64String(data);
                    sb.AppendLine($"<img class='excel-image' src='data:{mimeType};base64,{base64}' alt='Image {i + 1}'/>");
                }
                sb.AppendLine("</div>");
            }

            // Ajouter les dessins vectoriels pour les fichiers XLS
            if (Path.GetExtension(filePath).Equals(".xls", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var shapeSvgs = ExtractShapeSvgsFromXls(filePath);
                    foreach (var svg in shapeSvgs)
                    {
                        sb.AppendLine("<div class='images-section'>");
                        sb.AppendLine("<h3>Dessin intégré</h3>");
                        sb.AppendLine(svg);
                        sb.AppendLine("</div>");
                    }
                }
                catch { /* Ignorer les erreurs d'extraction de formes */ }
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Extrait les images intégrées dans un fichier Excel (XLS ou XLSX).
        /// </summary>
        internal static List<(string mimeType, byte[] data)> ExtractImages(string filePath)
        {
            var images = new List<(string, byte[])>();
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                if (ext == ".xlsx")
                    ExtractImagesFromXlsx(filePath, images);
                else if (ext == ".xls")
                    ExtractImagesFromXls(filePath, images);
            }
            catch
            {
                // Ignorer silencieusement les erreurs d'extraction d'images
            }

            return images;
        }

        /// <summary>
        /// Extrait les images d'un fichier XLSX (format ZIP) en lisant les entrées xl/media/.
        /// </summary>
        static void ExtractImagesFromXlsx(string filePath, List<(string, byte[])> images)
        {
            using var zip = ZipFile.OpenRead(filePath);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.Length == 0) continue;

                string? mime = GetMimeFromExtension(Path.GetExtension(entry.Name));
                if (mime == null) continue;

                using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                entryStream.CopyTo(ms);
                images.Add((mime, ms.ToArray()));
            }
        }

        /// <summary>
        /// Extrait les images d'un fichier XLS (BIFF8/OLE2) en parsant la structure OLE2,
        /// puis les enregistrements BIFF8 MsoDrawingGroup, et enfin les BLIPs OfficeArt.
        /// </summary>
        static void ExtractImagesFromXls(string filePath, List<(string, byte[])> images)
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);

            // Étape 1 : Extraire le flux Workbook depuis le document composé OLE2
            byte[]? workbookStream = ExtractOle2Stream(fileBytes);
            if (workbookStream == null)
            {
                // Fallback : scan brut des octets (ancien comportement)
                ScanRawBytesForImages(fileBytes, images);
                return;
            }

            // Étape 2 : Parser les enregistrements BIFF8 pour obtenir les données DrawingGroup
            byte[]? drawingData = ExtractBiff8DrawingGroupData(workbookStream);
            if (drawingData == null || drawingData.Length == 0)
            {
                // Fallback : scan brut sur le flux Workbook reconstitué
                ScanRawBytesForImages(workbookStream, images);
                return;
            }

            // Étape 3 : Extraire les BLIPs OfficeArt
            ExtractBlipsFromOfficeArt(drawingData, images);

            // Si aucune image trouvée via OfficeArt, fallback sur scan brut des données drawing
            if (images.Count == 0)
                ScanRawBytesForImages(drawingData, images);
        }

        /// <summary>
        /// Scan brut des signatures PNG/JPEG dans un tableau d'octets (fallback).
        /// </summary>
        static void ScanRawBytesForImages(byte[] data, List<(string, byte[])> images)
        {
            byte[] pngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            byte[] pngEnd = { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
            byte[] jpgSig = { 0xFF, 0xD8, 0xFF };
            byte[] jpgEnd = { 0xFF, 0xD9 };

            int pos = 0;
            while (pos < data.Length)
            {
                int start = IndexOf(data, pngSig, pos);
                if (start < 0) break;
                int end = IndexOf(data, pngEnd, start + pngSig.Length);
                if (end < 0) break;
                end += pngEnd.Length;
                int length = end - start;
                // Vérifier taille minimale + signature IHDR pour éviter les faux positifs
                if (length > 256 && start + 12 + 4 <= data.Length
                    && data[start + 12] == 0x49 && data[start + 13] == 0x48
                    && data[start + 14] == 0x44 && data[start + 15] == 0x52)
                {
                    byte[] imgData = new byte[length];
                    Array.Copy(data, start, imgData, 0, length);
                    images.Add(("image/png", imgData));
                }
                pos = end;
            }

            pos = 0;
            while (pos < data.Length)
            {
                int start = IndexOf(data, jpgSig, pos);
                if (start < 0) break;
                // Vérifier que le 4e octet est un marqueur JPEG valide (0xC0..0xFE)
                if (start + 3 < data.Length && data[start + 3] < 0xC0)
                {
                    pos = start + 3;
                    continue;
                }
                int end = IndexOf(data, jpgEnd, start + jpgSig.Length);
                if (end < 0) break;
                end += jpgEnd.Length;
                int length = end - start;
                if (length > 256)
                {
                    byte[] imgData = new byte[length];
                    Array.Copy(data, start, imgData, 0, length);
                    images.Add(("image/jpeg", imgData));
                }
                pos = end;
            }
        }

        #region OLE2 Compound Document parsing

        /// <summary>
        /// Extrait le flux "Workbook" (ou "Book") depuis un fichier OLE2 Compound Document.
        /// </summary>
        static byte[]? ExtractOle2Stream(byte[] fileBytes)
        {
            if (fileBytes.Length < 512) return null;
            // Vérifier la signature OLE2 : D0 CF 11 E0 A1 B1 1A E1
            if (fileBytes[0] != 0xD0 || fileBytes[1] != 0xCF ||
                fileBytes[2] != 0x11 || fileBytes[3] != 0xE0)
                return null;

            int sectorSize = 1 << BitConverter.ToUInt16(fileBytes, 30);
            int miniSectorSize = 1 << BitConverter.ToUInt16(fileBytes, 32);
            int firstDirSectorId = BitConverter.ToInt32(fileBytes, 48);
            int miniStreamCutoff = BitConverter.ToInt32(fileBytes, 56);
            int firstMiniFatSectorId = BitConverter.ToInt32(fileBytes, 60);
            int firstDifatSectorId = BitConverter.ToInt32(fileBytes, 68);
            int numDifatSectors = BitConverter.ToInt32(fileBytes, 72);

            // Construire la liste des secteurs FAT à partir du DIFAT
            var fatSectorIds = new List<int>();
            for (int i = 0; i < 109; i++)
            {
                int sid = BitConverter.ToInt32(fileBytes, 76 + i * 4);
                if (sid < 0) break;
                fatSectorIds.Add(sid);
            }
            int difatSid = firstDifatSectorId;
            for (int d = 0; d < numDifatSectors && difatSid >= 0; d++)
            {
                int off = Ole2SectorOffset(difatSid, sectorSize);
                if (off + sectorSize > fileBytes.Length) break;
                int entriesPerSector = sectorSize / 4 - 1;
                for (int i = 0; i < entriesPerSector; i++)
                {
                    int sid = BitConverter.ToInt32(fileBytes, off + i * 4);
                    if (sid < 0) break;
                    fatSectorIds.Add(sid);
                }
                difatSid = BitConverter.ToInt32(fileBytes, off + entriesPerSector * 4);
            }

            // Construire la FAT
            int entriesTotal = fatSectorIds.Count * (sectorSize / 4);
            var fat = new int[entriesTotal];
            int epsz = sectorSize / 4;
            for (int i = 0; i < fatSectorIds.Count; i++)
            {
                int off = Ole2SectorOffset(fatSectorIds[i], sectorSize);
                for (int j = 0; j < epsz; j++)
                    fat[i * epsz + j] = (off + j * 4 + 4 <= fileBytes.Length)
                        ? BitConverter.ToInt32(fileBytes, off + j * 4) : -1;
            }

            // Lire les entrées du répertoire
            byte[]? dirData = Ole2FollowChain(fileBytes, firstDirSectorId, fat, sectorSize, int.MaxValue);
            if (dirData == null) return null;

            int rootStartSector = -1, rootSize = 0;
            int targetStartSector = -1, targetSize = 0;

            for (int i = 0; i < dirData.Length / 128; i++)
            {
                int eo = i * 128;
                int nameBytes = BitConverter.ToUInt16(dirData, eo + 64);
                if (nameBytes == 0 || nameBytes > 64) continue;
                string name = Encoding.Unicode.GetString(dirData, eo, nameBytes).TrimEnd('\0');
                byte objType = dirData[eo + 66];
                int startSect = BitConverter.ToInt32(dirData, eo + 116);
                int size = BitConverter.ToInt32(dirData, eo + 120);

                if (i == 0 && objType == 5) { rootStartSector = startSect; rootSize = size; }
                if (name.Equals("Workbook", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Book", StringComparison.OrdinalIgnoreCase))
                { targetStartSector = startSect; targetSize = size; }
            }

            if (targetStartSector < 0 || targetSize <= 0) return null;

            // Lire via mini-stream si la taille est inférieure au seuil
            if (targetSize < miniStreamCutoff && rootStartSector >= 0)
            {
                byte[]? container = Ole2FollowChain(fileBytes, rootStartSector, fat, sectorSize, rootSize);
                if (container == null) return null;
                byte[]? miniFatData = Ole2FollowChain(fileBytes, firstMiniFatSectorId, fat, sectorSize, int.MaxValue);
                if (miniFatData == null) return null;
                int[] miniFat = new int[miniFatData.Length / 4];
                for (int i = 0; i < miniFat.Length; i++)
                    miniFat[i] = BitConverter.ToInt32(miniFatData, i * 4);
                return Ole2FollowMiniChain(container, targetStartSector, targetSize, miniFat, miniSectorSize);
            }

            return Ole2FollowChain(fileBytes, targetStartSector, fat, sectorSize, targetSize);
        }

        static int Ole2SectorOffset(int sectorId, int sectorSize) => (sectorId + 1) * sectorSize;

        static byte[]? Ole2FollowChain(byte[] fileBytes, int startSector, int[] fat, int sectorSize, int maxSize)
        {
            if (startSector < 0) return null;
            using var ms = new MemoryStream();
            int sector = startSector;
            int safety = fat.Length + 1;
            while (sector >= 0 && sector < fat.Length && safety-- > 0)
            {
                int off = Ole2SectorOffset(sector, sectorSize);
                if (off >= fileBytes.Length) break;
                int toWrite = Math.Min(sectorSize, fileBytes.Length - off);
                ms.Write(fileBytes, off, toWrite);
                if (ms.Length >= maxSize) break;
                sector = fat[sector];
                if (sector == -2 || sector == -1) break;
            }
            byte[] result = ms.ToArray();
            if (maxSize < int.MaxValue && result.Length > maxSize)
            {
                byte[] trimmed = new byte[maxSize];
                Array.Copy(result, trimmed, maxSize);
                return trimmed;
            }
            return result;
        }

        static byte[]? Ole2FollowMiniChain(byte[] container, int startSector, int size, int[] miniFat, int miniSectorSize)
        {
            using var ms = new MemoryStream();
            int sector = startSector;
            int remaining = size;
            int safety = miniFat.Length + 1;
            while (sector >= 0 && sector < miniFat.Length && remaining > 0 && safety-- > 0)
            {
                int off = sector * miniSectorSize;
                int toRead = Math.Min(miniSectorSize, remaining);
                if (off + toRead > container.Length) break;
                ms.Write(container, off, toRead);
                remaining -= toRead;
                sector = miniFat[sector];
                if (sector == -2 || sector == -1) break;
            }
            return ms.ToArray();
        }

        #endregion

        #region BIFF8 record parsing

        /// <summary>
        /// Parse les enregistrements BIFF8 pour extraire les données MsoDrawingGroup
        /// en concaténant les enregistrements CONTINUE associés.
        /// </summary>
        static byte[]? ExtractBiff8DrawingGroupData(byte[] workbookStream)
        {
            using var result = new MemoryStream();
            bool inDrawingGroup = false;
            int pos = 0;

            while (pos + 4 <= workbookStream.Length)
            {
                ushort recType = BitConverter.ToUInt16(workbookStream, pos);
                ushort recLen = BitConverter.ToUInt16(workbookStream, pos + 2);
                pos += 4;
                if (pos + recLen > workbookStream.Length) break;

                if (recType == 0x00EB) // MsoDrawingGroup
                {
                    inDrawingGroup = true;
                    result.Write(workbookStream, pos, recLen);
                }
                else if (recType == 0x003C && inDrawingGroup) // CONTINUE
                {
                    result.Write(workbookStream, pos, recLen);
                }
                else
                {
                    inDrawingGroup = false;
                }

                pos += recLen;
            }

            return result.Length > 0 ? result.ToArray() : null;
        }

        #endregion

        #region OfficeArt BLIP extraction

        /// <summary>
        /// Extrait les images (BLIPs) depuis les données OfficeArt du DrawingGroup.
        /// </summary>
        static void ExtractBlipsFromOfficeArt(byte[] data, List<(string, byte[])> images)
        {
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                ushort verInst = BitConverter.ToUInt16(data, pos);
                ushort recType = BitConverter.ToUInt16(data, pos + 2);
                int recLen = BitConverter.ToInt32(data, pos + 4);

                int ver = verInst & 0x0F;
                int inst = (verInst >> 4) & 0x0FFF;

                if (recLen < 0 || pos + 8 + recLen > data.Length)
                    break;

                // Les conteneurs (ver == 0x0F) contiennent des sous-enregistrements
                if (ver == 0x0F)
                {
                    pos += 8;
                    continue;
                }

                int blipDataStart = pos + 8;

                switch (recType)
                {
                    case 0xF01E: // OfficeArtBlipPNG
                    {
                        int hdr = 17; // rgbUid1(16) + tag(1)
                        if (inst == 0x6E1) hdr += 16; // rgbUid2
                        if (recLen > hdr)
                        {
                            byte[] img = new byte[recLen - hdr];
                            Array.Copy(data, blipDataStart + hdr, img, 0, img.Length);
                            images.Add(("image/png", img));
                        }
                        break;
                    }
                    case 0xF01D: // OfficeArtBlipJPEG
                    case 0xF02A: // OfficeArtBlipJPEG (alt)
                    {
                        int hdr = 17;
                        if (inst == 0x46B || inst == 0x6E3) hdr += 16;
                        if (recLen > hdr)
                        {
                            byte[] img = new byte[recLen - hdr];
                            Array.Copy(data, blipDataStart + hdr, img, 0, img.Length);
                            images.Add(("image/jpeg", img));
                        }
                        break;
                    }
                    case 0xF01F: // OfficeArtBlipDIB
                    {
                        int hdr = 17;
                        if (inst == 0x7A8) hdr += 16;
                        if (recLen > hdr)
                        {
                            byte[] img = new byte[recLen - hdr];
                            Array.Copy(data, blipDataStart + hdr, img, 0, img.Length);
                            images.Add(("image/bmp", img));
                        }
                        break;
                    }
                    case 0xF029: // OfficeArtBlipTIFF
                    {
                        int hdr = 17;
                        if (inst == 0x6E5) hdr += 16;
                        if (recLen > hdr)
                        {
                            byte[] img = new byte[recLen - hdr];
                            Array.Copy(data, blipDataStart + hdr, img, 0, img.Length);
                            images.Add(("image/tiff", img));
                        }
                        break;
                    }
                    // EMF (0xF01A) / WMF (0xF01B) / PICT (0xF01C) sont compressés zlib — ignorés
                }

                pos += 8 + recLen;
            }
        }

        #endregion

        #region Vector shape extraction

        sealed class XlsShape
        {
            public int ShapeType;
            public int Col1, Row1, Col2, Row2;
            public int Dx1, Dy1, Dx2, Dy2;
            public string? Text;
            public bool HasAnchor;
            public int ContainerIndex;
        }

        /// <summary>
        /// Extrait les formes vectorielles d'un fichier XLS et retourne les SVG correspondants.
        /// </summary>
        static List<string> ExtractShapeSvgsFromXls(string filePath)
        {
            var svgs = new List<string>();
            byte[] fileBytes = File.ReadAllBytes(filePath);
            byte[]? ws = ExtractOle2Stream(fileBytes);
            if (ws == null) return svgs;

            int pos = 0;
            MemoryStream? drawMs = null;
            List<string?>? objTexts = null;
            bool inDraw = false;
            int lastObjIdx = -1;
            bool waitTxoCont = false;
            int txoCharCount = 0;

            while (pos + 4 <= ws.Length)
            {
                ushort rt = BitConverter.ToUInt16(ws, pos);
                ushort rl = BitConverter.ToUInt16(ws, pos + 2);
                int d = pos + 4;
                if (d + rl > ws.Length) break;

                switch (rt)
                {
                    case 0x0809: // BOF
                        drawMs = new MemoryStream();
                        objTexts = new List<string?>();
                        inDraw = false;
                        waitTxoCont = false;
                        lastObjIdx = -1;
                        break;

                    case 0x000A: // EOF
                        if (drawMs != null && drawMs.Length > 0 && objTexts != null)
                        {
                            int spIdx = 0;
                            var shapes = new List<XlsShape>();
                            WalkOfficeArtForShapes(drawMs.ToArray(), 0, (int)drawMs.Length, shapes, ref spIdx);
                            foreach (var s in shapes)
                            {
                                // Le premier SpContainer (group shape) n'a pas de record OBJ,
                                // donc l'index dans objTexts est décalé de -1
                                int textIdx = s.ContainerIndex - 1;
                                if (textIdx >= 0 && textIdx < objTexts.Count)
                                    s.Text = objTexts[textIdx];
                            }
                            string svg = RenderShapesToSvg(shapes);
                            if (svg.Length > 0) svgs.Add(svg);
                        }
                        drawMs = null;
                        objTexts = null;
                        break;

                    case 0x00EC: // MsoDrawing
                        inDraw = true;
                        waitTxoCont = false;
                        drawMs?.Write(ws, d, rl);
                        break;

                    case 0x003C: // CONTINUE
                        if (waitTxoCont && rl > 0 && txoCharCount > 0)
                        {
                            byte flag = ws[d];
                            string txt = flag == 0
                                ? Encoding.GetEncoding(1252).GetString(ws, d + 1, Math.Min(txoCharCount, rl - 1))
                                : Encoding.Unicode.GetString(ws, d + 1, Math.Min(txoCharCount * 2, rl - 1));
                            if (lastObjIdx >= 0 && objTexts != null && lastObjIdx < objTexts.Count)
                                objTexts[lastObjIdx] = txt.TrimEnd('\0');
                            waitTxoCont = false;
                            inDraw = false;
                        }
                        else if (inDraw)
                        {
                            drawMs?.Write(ws, d, rl);
                        }
                        break;

                    case 0x005D: // OBJ
                        inDraw = false;
                        waitTxoCont = false;
                        objTexts?.Add(null);
                        lastObjIdx = (objTexts?.Count ?? 0) - 1;
                        break;

                    case 0x01B6: // TXO
                        inDraw = false;
                        waitTxoCont = false;
                        if (rl >= 12)
                        {
                            txoCharCount = BitConverter.ToUInt16(ws, d + 10);
                            if (txoCharCount > 0)
                                waitTxoCont = true;
                        }
                        break;

                    default:
                        // Ne pas réinitialiser inDraw ici — seuls OBJ/TXO doivent le faire.
                        // Des records BIFF8 inconnus peuvent apparaître dans le flux
                        // sans interrompre la collecte des données MsoDrawing.
                        break;
                }

                pos = d + rl;
            }

            return svgs;
        }

        static void WalkOfficeArtForShapes(byte[] data, int offset, int end, List<XlsShape> shapes, ref int spContainerIdx)
        {
            int pos = offset;
            while (pos + 8 <= end)
            {
                ushort vi = BitConverter.ToUInt16(data, pos);
                ushort rt = BitConverter.ToUInt16(data, pos + 2);
                int rl = BitConverter.ToInt32(data, pos + 4);
                int ver = vi & 0x0F;
                if (rl < 0 || (long)pos + 8 + rl > end) break;

                if (ver == 0x0F)
                {
                    if (rt == 0xF004) // SpContainer
                    {
                        var s = new XlsShape { ContainerIndex = spContainerIdx++ };
                        ReadSpContainerForShape(data, pos + 8, pos + 8 + rl, s);
                        if (s.HasAnchor)
                            shapes.Add(s);
                    }
                    else
                    {
                        WalkOfficeArtForShapes(data, pos + 8, pos + 8 + rl, shapes, ref spContainerIdx);
                    }
                }
                pos += 8 + rl;
            }
        }

        static void ReadSpContainerForShape(byte[] data, int offset, int end, XlsShape s)
        {
            int pos = offset;
            while (pos + 8 <= end)
            {
                ushort vi = BitConverter.ToUInt16(data, pos);
                ushort rt = BitConverter.ToUInt16(data, pos + 2);
                int rl = BitConverter.ToInt32(data, pos + 4);
                int ver = vi & 0x0F;
                int inst = (vi >> 4) & 0x0FFF;
                if (rl < 0 || (long)pos + 8 + rl > end) break;

                if (ver == 0x0F) { pos += 8 + rl; continue; }

                switch (rt)
                {
                    case 0xF00A: // Sp — shape type in inst field
                        s.ShapeType = inst;
                        break;
                    case 0xF010: // ClientAnchor — position (col/row)
                        if (rl >= 18)
                        {
                            int o = pos + 8;
                            s.Col1 = BitConverter.ToUInt16(data, o + 2);
                            s.Dx1  = BitConverter.ToUInt16(data, o + 4);
                            s.Row1 = BitConverter.ToUInt16(data, o + 6);
                            s.Dy1  = BitConverter.ToUInt16(data, o + 8);
                            s.Col2 = BitConverter.ToUInt16(data, o + 10);
                            s.Dx2  = BitConverter.ToUInt16(data, o + 12);
                            s.Row2 = BitConverter.ToUInt16(data, o + 14);
                            s.Dy2  = BitConverter.ToUInt16(data, o + 16);
                            s.HasAnchor = true;
                        }
                        break;
                    case 0xF00F: // ChildAnchor — position (coordonnées groupe)
                        if (rl >= 16 && !s.HasAnchor)
                        {
                            // Coordonnées dans le système du groupe parent (EMU-like)
                            int o = pos + 8;
                            int xL = BitConverter.ToInt32(data, o);
                            int yT = BitConverter.ToInt32(data, o + 4);
                            int xR = BitConverter.ToInt32(data, o + 8);
                            int yB = BitConverter.ToInt32(data, o + 12);
                            // Conversion approximative : les coordonnées enfant sont souvent en
                            // unités internes du groupe. On les convertit en pseudo-col/row.
                            s.Col1 = 0; s.Row1 = 0; s.Col2 = 0; s.Row2 = 0;
                            s.Dx1 = 0; s.Dy1 = 0; s.Dx2 = 0; s.Dy2 = 0;
                            s.HasAnchor = true;
                            // Stocker directement en pixels via champs existants
                            // On réutilise Col/Row comme coordonnées brutes (unités /20 pour px)
                            s.Col1 = xL / 20; s.Row1 = yT / 20;
                            s.Col2 = xR / 20; s.Row2 = yB / 20;
                            // Marquer comme ancre enfant via Dx1 = -1
                            s.Dx1 = -1;
                        }
                        break;
                }
                pos += 8 + rl;
            }
        }

        static string RenderShapesToSvg(List<XlsShape> shapes)
        {
            if (shapes.Count == 0) return "";

            const double cw = 64.0; // largeur colonne par défaut en pixels
            const double rh = 20.0; // hauteur ligne par défaut en pixels

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var s in shapes)
            {
                double x1, y1, x2, y2;
                if (s.Dx1 == -1) // ChildAnchor — coordonnées déjà en pixels
                {
                    x1 = s.Col1; y1 = s.Row1; x2 = s.Col2; y2 = s.Row2;
                }
                else
                {
                    x1 = s.Col1 * cw + s.Dx1 * cw / 1024.0;
                    y1 = s.Row1 * rh + s.Dy1 * rh / 256.0;
                    x2 = s.Col2 * cw + s.Dx2 * cw / 1024.0;
                    y2 = s.Row2 * rh + s.Dy2 * rh / 256.0;
                }
                minX = Math.Min(minX, Math.Min(x1, x2));
                minY = Math.Min(minY, Math.Min(y1, y2));
                maxX = Math.Max(maxX, Math.Max(x1, x2));
                maxY = Math.Max(maxY, Math.Max(y1, y2));
            }

            if (maxX - minX < 1 || maxY - minY < 1) return "";

            double pad = 12;
            double svgW = maxX - minX + pad * 2;
            double svgH = maxY - minY + pad * 2;

            var sb = new StringBuilder();
            sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {SvgN(svgW)} {SvgN(svgH)}' width='100%' style='max-height:80vh; background:white;'>");

            foreach (var s in shapes)
            {
                double x1, y1, x2, y2;
                if (s.Dx1 == -1) // ChildAnchor
                {
                    x1 = s.Col1 - minX + pad; y1 = s.Row1 - minY + pad;
                    x2 = s.Col2 - minX + pad; y2 = s.Row2 - minY + pad;
                }
                else
                {
                    x1 = s.Col1 * cw + s.Dx1 * cw / 1024.0 - minX + pad;
                    y1 = s.Row1 * rh + s.Dy1 * rh / 256.0 - minY + pad;
                    x2 = s.Col2 * cw + s.Dx2 * cw / 1024.0 - minX + pad;
                    y2 = s.Row2 * rh + s.Dy2 * rh / 256.0 - minY + pad;
                }
                double w = x2 - x1;
                double h = y2 - y1;

                switch (s.ShapeType)
                {
                    case 1: // Rectangle
                        sb.AppendLine($"  <rect x='{SvgN(x1)}' y='{SvgN(y1)}' width='{SvgN(Math.Abs(w))}' height='{SvgN(Math.Abs(h))}' fill='none' stroke='black' stroke-width='1.5'/>");
                        if (!string.IsNullOrWhiteSpace(s.Text))
                            sb.AppendLine($"  <text x='{SvgN(x1 + w / 2)}' y='{SvgN(y1 + h / 2)}' text-anchor='middle' dominant-baseline='central' font-size='11' fill='black'>{HtmlEncode(s.Text)}</text>");
                        break;

                    case 20: // Line
                    case 32: // StraightConnector1
                        sb.AppendLine($"  <line x1='{SvgN(x1)}' y1='{SvgN(y1)}' x2='{SvgN(x2)}' y2='{SvgN(y2)}' stroke='black' stroke-width='1'/>");
                        break;

                    case 202: // TextBox
                        if (!string.IsNullOrWhiteSpace(s.Text))
                            sb.AppendLine($"  <text x='{SvgN(x1 + w / 2)}' y='{SvgN(y1 + h / 2)}' text-anchor='middle' dominant-baseline='central' font-size='13' font-weight='bold' fill='black'>{HtmlEncode(s.Text)}</text>");
                        break;

                    default:
                        if (Math.Abs(w) > 2 && Math.Abs(h) > 2)
                            sb.AppendLine($"  <rect x='{SvgN(Math.Min(x1, x2))}' y='{SvgN(Math.Min(y1, y2))}' width='{SvgN(Math.Abs(w))}' height='{SvgN(Math.Abs(h))}' fill='none' stroke='#888' stroke-width='1' stroke-dasharray='3'/>");
                        else if (Math.Abs(w) > 1 || Math.Abs(h) > 1)
                            sb.AppendLine($"  <line x1='{SvgN(x1)}' y1='{SvgN(y1)}' x2='{SvgN(x2)}' y2='{SvgN(y2)}' stroke='#888' stroke-width='1'/>");
                        break;
                }
            }

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        static string SvgN(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        #endregion

        /// <summary>
        /// Recherche la première occurrence d'un motif d'octets dans un tableau à partir d'un offset donné.
        /// </summary>
        static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
        {
            int limit = haystack.Length - needle.Length;
            for (int i = startIndex; i <= limit; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }

        static string? GetMimeFromExtension(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => null
            };
        }

        static string HtmlEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
