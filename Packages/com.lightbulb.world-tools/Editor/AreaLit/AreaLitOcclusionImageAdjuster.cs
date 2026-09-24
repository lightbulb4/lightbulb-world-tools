using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    [Serializable]
    internal sealed class ManualAdjustmentRecord
    {
        public string assetGuid;
        public string assetPath;
        public string backupAbsolutePath;
        public string adjustedBeforeRestoreAbsolutePath;
        public string createdUtc;
        public string state;
        public string error;
        public float brightness = 1f;
        public float contrast = 1f;
    }

    internal static class AreaLitOcclusionImageAdjuster
    {
        private const string ShaderName = "Hidden/Lightbulb/AreaLitOcclusionAdjust";
        private const string ManualAdjustmentJournalName = "last-manual-adjustment.json";
        private static readonly int BrightnessProperty = Shader.PropertyToID("_Brightness");
        private static readonly int ContrastProperty = Shader.PropertyToID("_Contrast");
        private static readonly int EncodeForDisplayProperty = Shader.PropertyToID("_EncodeForDisplay");

        private static string ManualAdjustmentRoot
        {
            get { return Path.Combine(AreaLitOcclusionPaths.ProjectRoot, "Library", "AreaLitOcclusion", "ManualAdjustmentBackups"); }
        }

        private static string ManualAdjustmentJournalPath
        {
            get { return Path.Combine(AreaLitOcclusionPaths.ProjectRoot, "Library", "AreaLitOcclusion", ManualAdjustmentJournalName); }
        }

        public static bool IsNeutral(float brightness, float contrast)
        {
            return Mathf.Approximately(NormalizeBrightness(brightness), 1f) &&
                   Mathf.Approximately(NormalizeContrast(contrast), 1f);
        }

        public static float NormalizeBrightness(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 1f : Mathf.Clamp(value, 0f, 5f);
        }

        public static float NormalizeContrast(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 1f : Mathf.Clamp(value, 0f, 2f);
        }

        public static Material CreateMaterial()
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException("The AreaLit occlusion adjustment shader could not be loaded.");
            }

            return new Material(shader)
            {
                name = "AreaLit Occlusion Adjustment (Editor Only)",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        public static void ConfigureMaterial(
            Material material,
            float brightness,
            float contrast,
            bool encodeForDisplay = true)
        {
            if (material == null) throw new ArgumentNullException("material");
            material.SetFloat(BrightnessProperty, NormalizeBrightness(brightness));
            material.SetFloat(ContrastProperty, NormalizeContrast(contrast));
            material.SetFloat(EncodeForDisplayProperty, encodeForDisplay ? 1f : 0f);
        }

        public static string GetInPlaceAdjustmentBlockReason(Texture texture)
        {
            if (texture == null) return "Choose an occlusion-map asset to adjust.";
            if (!(texture is Texture2D)) return "Only 2D texture assets can be adjusted.";

            var assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return "The preview must be a writable texture under Assets.";
            }

            var extension = (Path.GetExtension(assetPath) ?? string.Empty).ToLowerInvariant();
            if (extension != ".hdr" && extension != ".exr" && extension != ".png")
            {
                return "Saving adjustments supports HDR, EXR, and PNG textures.";
            }

            var absolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            if (!File.Exists(absolutePath)) return "The previewed texture file no longer exists.";
            if ((File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) != 0)
            {
                return "The previewed texture file is read-only.";
            }

            return string.Empty;
        }

        public static ManualAdjustmentRecord ApplyToAssetInPlace(
            Texture texture,
            float brightness,
            float contrast)
        {
            var blockReason = GetInPlaceAdjustmentBlockReason(texture);
            if (!string.IsNullOrEmpty(blockReason)) throw new InvalidOperationException(blockReason);

            var assetPath = AssetDatabase.GetAssetPath(texture);
            var sourceAbsolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            var backupDirectory = Path.Combine(
                ManualAdjustmentRoot,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(backupDirectory);
            var backupAbsolutePath = Path.Combine(backupDirectory, Path.GetFileName(sourceAbsolutePath));
            File.Copy(sourceAbsolutePath, backupAbsolutePath, false);

            var record = new ManualAdjustmentRecord
            {
                assetGuid = AssetDatabase.AssetPathToGUID(assetPath),
                assetPath = assetPath,
                backupAbsolutePath = backupAbsolutePath,
                createdUtc = DateTime.UtcNow.ToString("O"),
                state = "Prepared",
                brightness = NormalizeBrightness(brightness),
                contrast = NormalizeContrast(contrast)
            };
            SaveManualAdjustmentRecord(record);

            try
            {
                WriteAdjustedTexture(assetPath, assetPath, record.brightness, record.contrast);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath) == null)
                {
                    throw new InvalidOperationException("Unity could not reimport the adjusted texture.");
                }

                record.state = "Applied";
                SaveManualAdjustmentRecord(record);
                return record;
            }
            catch (Exception applyException)
            {
                try
                {
                    RestoreAssetFile(record, assetPath);
                    record.state = "RolledBack";
                    record.error = applyException.ToString();
                    SaveManualAdjustmentRecord(record);
                }
                catch (Exception restoreException)
                {
                    record.state = "RecoveryRequired";
                    record.error = applyException + "\nAutomatic restore failed:\n" + restoreException;
                    SaveManualAdjustmentRecord(record);
                    throw new InvalidOperationException(
                        "Saving the adjustment failed and automatic restore also failed. The original file is preserved at " +
                        backupAbsolutePath,
                        new AggregateException(applyException, restoreException));
                }

                throw new InvalidOperationException(
                    "Saving the adjustment failed. The original texture was restored automatically.",
                    applyException);
            }
        }

        public static bool TryGetRestorableManualAdjustment(out ManualAdjustmentRecord record)
        {
            record = null;
            if (!File.Exists(ManualAdjustmentJournalPath)) return false;

            try
            {
                record = JsonUtility.FromJson<ManualAdjustmentRecord>(File.ReadAllText(ManualAdjustmentJournalPath));
                if (record == null ||
                    (record.state != "Applied" && record.state != "Prepared" &&
                     record.state != "Restoring" && record.state != "RecoveryRequired") ||
                    string.IsNullOrEmpty(record.backupAbsolutePath) ||
                    !File.Exists(record.backupAbsolutePath))
                {
                    record = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[AreaLit Occlusion] Could not read the last manual-adjustment recovery record.\n" + exception);
                record = null;
                return false;
            }
        }

        public static ManualAdjustmentRecord RestoreLastManualAdjustment()
        {
            ManualAdjustmentRecord record;
            if (!TryGetRestorableManualAdjustment(out record))
            {
                throw new InvalidOperationException("No saved manual adjustment is available to restore.");
            }

            var assetPath = string.IsNullOrEmpty(record.assetGuid)
                ? string.Empty
                : AssetDatabase.GUIDToAssetPath(record.assetGuid);
            if (string.IsNullOrEmpty(assetPath)) assetPath = record.assetPath;
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                AssetImporter.GetAtPath(assetPath) == null)
            {
                throw new InvalidOperationException(
                    "The adjusted texture asset can no longer be found. Its original file remains at " +
                    record.backupAbsolutePath);
            }

            var destinationAbsolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            var adjustedBackupPath = Path.Combine(
                Path.GetDirectoryName(record.backupAbsolutePath),
                "adjusted-before-revert" + Path.GetExtension(destinationAbsolutePath));
            File.Copy(destinationAbsolutePath, adjustedBackupPath, true);
            record.adjustedBeforeRestoreAbsolutePath = adjustedBackupPath;
            record.state = "Restoring";
            SaveManualAdjustmentRecord(record);

            try
            {
                RestoreAssetFile(record, assetPath);
                record.assetPath = assetPath;
                record.state = "Restored";
                SaveManualAdjustmentRecord(record);
                return record;
            }
            catch (Exception exception)
            {
                record.state = "RecoveryRequired";
                record.error = exception.ToString();
                SaveManualAdjustmentRecord(record);
                throw new InvalidOperationException(
                    "The original texture could not be restored automatically. Its backup remains at " +
                    record.backupAbsolutePath,
                    exception);
            }
        }

        public static void WriteAdjustedTexture(
            string sourceAssetPath,
            string destinationAssetPath,
            float brightness,
            float contrast)
        {
            var source = AssetDatabase.LoadAssetAtPath<Texture2D>(sourceAssetPath);
            if (source == null)
            {
                throw new InvalidOperationException("The baked occlusion texture could not be loaded: " + sourceAssetPath);
            }

            var extension = (Path.GetExtension(destinationAssetPath) ?? string.Empty).ToLowerInvariant();
            if (extension != ".hdr" && extension != ".exr" && extension != ".png")
            {
                throw new InvalidOperationException(
                    "Brightness and contrast output supports Bakery HDR, EXR, or PNG textures. Received: " + extension);
            }

            var destinationAbsolutePath = AreaLitOcclusionPaths.ToAbsolutePath(destinationAssetPath);
            var temporaryPath = destinationAbsolutePath + ".adjusting.tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(destinationAbsolutePath));
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            if (extension == ".hdr")
            {
                try
                {
                    WriteAdjustedRadianceHdr(
                        AreaLitOcclusionPaths.ToAbsolutePath(sourceAssetPath),
                        temporaryPath,
                        NormalizeBrightness(brightness),
                        NormalizeContrast(contrast));
                    AreaLitOcclusionAssetFile.CommitTemporaryFile(temporaryPath, destinationAbsolutePath);
                    return;
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }

            Material material = null;
            RenderTexture renderTexture = null;
            Texture2D readable = null;
            var previousActive = RenderTexture.active;
            try
            {
                material = CreateMaterial();
                ConfigureMaterial(material, brightness, contrast, false);

                var renderFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)
                    ? RenderTextureFormat.ARGBFloat
                    : RenderTextureFormat.ARGBHalf;
                if (!SystemInfo.SupportsRenderTextureFormat(renderFormat))
                {
                    throw new InvalidOperationException("This graphics device cannot create an HDR texture for occlusion adjustments.");
                }

                renderTexture = RenderTexture.GetTemporary(
                    source.width,
                    source.height,
                    0,
                    renderFormat,
                    RenderTextureReadWrite.Linear);
                Graphics.Blit(source, renderTexture, material);

                RenderTexture.active = renderTexture;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true)
                {
                    name = source.name + " (Adjusted Readback)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                readable.Apply(false, false);

                WriteEncodedTexture(readable, temporaryPath, extension);
                AreaLitOcclusionAssetFile.CommitTemporaryFile(temporaryPath, destinationAbsolutePath);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void WriteEncodedTexture(Texture2D texture, string path, string extension)
        {
            extension = (extension ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".exr":
                    File.WriteAllBytes(
                        path,
                        texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
                    return;
                case ".png":
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                    return;
                default:
                    throw new ArgumentOutOfRangeException("extension", extension, "Unsupported adjusted texture extension.");
            }
        }

        private static void CommitTemporaryFile(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }

        private static void RestoreAssetFile(ManualAdjustmentRecord record, string assetPath)
        {
            if (record == null || string.IsNullOrEmpty(record.backupAbsolutePath) || !File.Exists(record.backupAbsolutePath))
            {
                throw new FileNotFoundException("The original manual-adjustment backup is missing.");
            }

            var destinationAbsolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            var temporaryPath = destinationAbsolutePath + ".restoring.tmp";
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                File.Copy(record.backupAbsolutePath, temporaryPath, false);
                AreaLitOcclusionAssetFile.CommitTemporaryFile(temporaryPath, destinationAbsolutePath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void SaveManualAdjustmentRecord(ManualAdjustmentRecord record)
        {
            var directory = Path.GetDirectoryName(ManualAdjustmentJournalPath);
            Directory.CreateDirectory(directory);
            var temporaryPath = ManualAdjustmentJournalPath + ".tmp";
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                File.WriteAllText(temporaryPath, JsonUtility.ToJson(record, true), new UTF8Encoding(false));
                CommitTemporaryFile(temporaryPath, ManualAdjustmentJournalPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void WriteAdjustedRadianceHdr(
            string sourcePath,
            string destinationPath,
            float brightness,
            float contrast)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The source HDR file is missing.", sourcePath);
            }

            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var magic = ReadAsciiLine(input);
                if (magic != "#?RADIANCE" && magic != "#?RGBE")
                {
                    throw new InvalidDataException("The source is not a Radiance HDR file: " + sourcePath);
                }
                WriteAsciiLine(output, magic);

                var formatFound = false;
                while (true)
                {
                    var headerLine = ReadAsciiLine(input);
                    if (headerLine == null)
                    {
                        throw new EndOfStreamException("The Radiance HDR header ended unexpectedly.");
                    }
                    WriteAsciiLine(output, headerLine);
                    if (string.Equals(headerLine, "FORMAT=32-bit_rle_rgbe", StringComparison.OrdinalIgnoreCase))
                    {
                        formatFound = true;
                    }
                    if (headerLine.Length == 0) break;
                }

                if (!formatFound)
                {
                    throw new InvalidDataException("The Radiance HDR does not use the supported RGBE format.");
                }

                var resolutionLine = ReadAsciiLine(input);
                int width;
                int height;
                if (!TryParseRadianceResolution(resolutionLine, out width, out height))
                {
                    throw new InvalidDataException("Unsupported Radiance HDR resolution line: " + resolutionLine);
                }
                WriteAsciiLine(output, resolutionLine);

                var scanline = new byte[width * 4];
                var channel = new byte[width];
                for (var y = 0; y < height; y++)
                {
                    ReadRadianceScanline(input, scanline, width);
                    for (var x = 0; x < width; x++)
                    {
                        var offset = x * 4;
                        var source = DecodeRgbe(scanline, offset);
                        source.r = AdjustHdrChannel(source.r, brightness, contrast);
                        source.g = AdjustHdrChannel(source.g, brightness, contrast);
                        source.b = AdjustHdrChannel(source.b, brightness, contrast);
                        EncodeRgbe(source, scanline, offset);
                    }
                    WriteRadianceScanline(output, scanline, channel, width);
                }
            }
        }

        private static string ReadAsciiLine(Stream stream)
        {
            var line = new StringBuilder();
            while (true)
            {
                var next = stream.ReadByte();
                if (next < 0) return line.Length == 0 ? null : line.ToString();
                if (next == '\n') return line.ToString();
                if (next != '\r') line.Append((char)next);
            }
        }

        private static void WriteAsciiLine(Stream stream, string line)
        {
            var bytes = Encoding.ASCII.GetBytes((line ?? string.Empty) + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        private static bool TryParseRadianceResolution(string line, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(line)) return false;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4 &&
                   (parts[0] == "-Y" || parts[0] == "+Y") &&
                   int.TryParse(parts[1], out height) &&
                   height > 0 &&
                   (parts[2] == "+X" || parts[2] == "-X") &&
                   int.TryParse(parts[3], out width) &&
                   width > 0;
        }

        private static void ReadRadianceScanline(Stream stream, byte[] scanline, int width)
        {
            if (width < 8 || width > 32767)
            {
                ReadExactly(stream, scanline, 0, scanline.Length);
                return;
            }

            var marker0 = ReadRequiredByte(stream);
            var marker1 = ReadRequiredByte(stream);
            var marker2 = ReadRequiredByte(stream);
            var marker3 = ReadRequiredByte(stream);
            if (marker0 != 2 || marker1 != 2 || ((marker2 << 8) | marker3) != width)
            {
                throw new InvalidDataException("The Radiance HDR uses an unsupported scanline encoding.");
            }

            for (var component = 0; component < 4; component++)
            {
                var x = 0;
                while (x < width)
                {
                    var packet = ReadRequiredByte(stream);
                    if (packet == 0)
                    {
                        throw new InvalidDataException("The Radiance HDR contains a zero-length scanline packet.");
                    }

                    var count = packet > 128 ? packet - 128 : packet;
                    if (x + count > width)
                    {
                        throw new InvalidDataException("The Radiance HDR scanline packet exceeds its declared width.");
                    }

                    if (packet > 128)
                    {
                        var value = (byte)ReadRequiredByte(stream);
                        for (var index = 0; index < count; index++)
                        {
                            scanline[(x + index) * 4 + component] = value;
                        }
                    }
                    else
                    {
                        for (var index = 0; index < count; index++)
                        {
                            scanline[(x + index) * 4 + component] = (byte)ReadRequiredByte(stream);
                        }
                    }
                    x += count;
                }
            }
        }

        private static int ReadRequiredByte(Stream stream)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException("The Radiance HDR ended inside a scanline.");
            return value;
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException("The Radiance HDR ended inside a scanline.");
                offset += read;
                count -= read;
            }
        }

        private static Color DecodeRgbe(byte[] source, int offset)
        {
            var exponent = source[offset + 3];
            if (exponent == 0) return Color.black;

            var scale = Math.Pow(2.0, exponent - (128 + 8));
            return new Color(
                (float)(source[offset] * scale),
                (float)(source[offset + 1] * scale),
                (float)(source[offset + 2] * scale),
                1f);
        }

        private static float AdjustHdrChannel(float source, float brightness, float contrast)
        {
            if (source < 0.000001f) return 0f;
            var adjusted = ((source * brightness) - 0.5f) * contrast + 0.5f;
            if (float.IsNaN(adjusted) || adjusted <= 0f) return 0f;
            return Mathf.Min(adjusted, 1f);
        }

        private static void WriteRadianceScanline(Stream stream, byte[] scanline, byte[] channel, int width)
        {
            if (width < 8 || width > 32767)
            {
                stream.Write(scanline, 0, scanline.Length);
                return;
            }

            stream.WriteByte(2);
            stream.WriteByte(2);
            stream.WriteByte((byte)(width >> 8));
            stream.WriteByte((byte)(width & 255));
            for (var component = 0; component < 4; component++)
            {
                for (var x = 0; x < width; x++)
                {
                    channel[x] = scanline[x * 4 + component];
                }
                WriteRleChannel(stream, channel);
            }
        }

        private static void EncodeRgbe(Color color, byte[] destination, int offset)
        {
            var red = SanitizeChannel(color.r);
            var green = SanitizeChannel(color.g);
            var blue = SanitizeChannel(color.b);
            var maximum = Math.Max(red, Math.Max(green, blue));
            if (maximum < 1e-32)
            {
                destination[offset] = 0;
                destination[offset + 1] = 0;
                destination[offset + 2] = 0;
                destination[offset + 3] = 0;
                return;
            }

            var exponent = Math.Max(-127, Math.Min(127, (int)Math.Floor(Math.Log(maximum, 2.0)) + 1));
            var scale = Math.Pow(2.0, -exponent) * 256.0;
            destination[offset] = ToByte(red * scale);
            destination[offset + 1] = ToByte(green * scale);
            destination[offset + 2] = ToByte(blue * scale);
            destination[offset + 3] = (byte)(exponent + 128);
        }

        private static double SanitizeChannel(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 0.0;
            if (float.IsInfinity(value)) return 1e30;
            return Math.Min(value, 1e30);
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, (int)value));
        }

        private static void WriteRleChannel(Stream stream, byte[] values)
        {
            var index = 0;
            while (index < values.Length)
            {
                var literalStart = index;
                var literalCount = 0;
                while (index < values.Length && literalCount < 128)
                {
                    var runLength = CountRun(values, index);
                    if (runLength >= 4) break;
                    index++;
                    literalCount++;
                }

                if (literalCount > 0)
                {
                    stream.WriteByte((byte)literalCount);
                    stream.Write(values, literalStart, literalCount);
                    continue;
                }

                var count = CountRun(values, index);
                stream.WriteByte((byte)(128 + count));
                stream.WriteByte(values[index]);
                index += count;
            }
        }

        private static int CountRun(byte[] values, int start)
        {
            var count = 1;
            while (start + count < values.Length && count < 127 && values[start + count] == values[start])
            {
                count++;
            }
            return count;
        }
    }
}
