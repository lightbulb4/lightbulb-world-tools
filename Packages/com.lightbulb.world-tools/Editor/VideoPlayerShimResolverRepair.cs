using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lightbulb.WorldTools
{
    // Only a byte-identified upstream release is patched; custom source is never guessed at.
    internal static class VideoPlayerShimResolverRepair
    {
        internal const string PackageName = "dev.architech.videoplayershim";
        internal const string SupportedVersion = "1.5.0";
        internal const string ResolverRelativePath = "Editor/PlayModeUrlResolverShim.cs";
        private const string OriginalHash = "1eac5c7a5ff9a8684fcd7b8900a9b8c4bfc2353f7ccca1ca31741b2fb9f90a8e";
        private const string RepairedHash = "f745c4591ff2a51a28cc6eb939656f9de64477b721ef48de29db1d5f41e36759";
        // Earlier local repair: early protocol bypass plus separate stdout URL and terminating errors.
        private const string EarlierRepairHash = "dec5dada8e9146e0c2fb8bb08d3b60c04e0909a9cd0f66139792056c6eb2bc60";
        private const string ReplacementStart = "        static void ResolveURLCallback(";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        // Returns the original-file backup path, or null when the known repair is already present.
        internal static string Apply(string projectRoot, string packageRoot, string version)
        {
            if (version != SupportedVersion)
                throw new InvalidOperationException("Only VideoPlayerShim 1.5.0 is supported. This version was not changed.");

            projectRoot = Path.GetFullPath(projectRoot);
            packageRoot = Path.GetFullPath(packageRoot);
            var expectedRoot = Path.Combine(projectRoot, "Packages", PackageName);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(packageRoot.TrimEnd(Path.DirectorySeparatorChar), expectedRoot, comparison))
                throw new InvalidOperationException("Only the project's embedded VideoPlayerShim package can be repaired.");

            var target = Path.Combine(packageRoot, ResolverRelativePath);
            EnsureNoLinks(target);
            if (!File.Exists(target))
                throw new InvalidOperationException("VideoPlayerShim's URL resolver script is missing. Nothing was changed.");
            if (new FileInfo(target).Length > 1024 * 1024)
                throw new InvalidOperationException("The resolver script is unexpectedly large. Nothing was changed.");

            byte[] original = File.ReadAllBytes(target);
            bool hasBom = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
            int offset = hasBom ? 3 : 0;
            string source = Utf8.GetString(original, offset, original.Length - offset);
            string normalized = source.Replace("\r\n", "\n");
            string hash = Hash(normalized);
            if (hash == RepairedHash || hash == EarlierRepairHash)
                return null;
            if (hash != OriginalHash)
                throw new InvalidOperationException("The resolver does not match the known broken 1.5.0 release or a recognized repair. Custom or unfamiliar code was left untouched.");
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new InvalidOperationException("The resolver script is read-only. Check it out before running this command.");

            bool crlf = source.Contains("\r\n");
            if (crlf && source.Replace("\r\n", "").Contains("\n"))
                throw new InvalidOperationException("The resolver has mixed line endings. Nothing was changed.");

            int start = normalized.IndexOf(ReplacementStart, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("The expected resolver methods were not found. Nothing was changed.");
            string repaired = normalized.Substring(0, start) + RepairedMethods.Replace("\r\n", "\n");
            if (Hash(repaired) != RepairedHash)
                throw new InvalidOperationException("The repair template failed its integrity check. Nothing was changed.");
            if (crlf)
                repaired = repaired.Replace("\n", "\r\n");

            byte[] bytes = Utf8.GetBytes(repaired);
            if (hasBom)
                bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray();

            var backupDirectory = Path.Combine(projectRoot, "Library", "LightbulbWorldTools", "Backups",
                "VideoPlayerShim", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(backupDirectory, "PlayModeUrlResolverShim.cs");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            EnsureNoLinks(backup);
            Directory.CreateDirectory(backupDirectory);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                EnsureNoLinks(target);
                if (!File.ReadAllBytes(target).SequenceEqual(original))
                    throw new IOException("The resolver changed during the repair. Nothing was replaced.");

                // Atomic replacement retains the previous file as a backup; never truncate the original.
                File.Replace(temporary, target, backup);
                if (!File.ReadAllBytes(target).SequenceEqual(bytes))
                    throw new IOException("The resolver was replaced but verification failed. Restore the original from: " + backup);
                return backup;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        private static void EnsureNoLinks(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked files or folders are not supported. Nothing was changed.");
            }
        }

        // Derived from ArchiTech.VideoPlayerShim 1.5.0 (ISC); see THIRD_PARTY_NOTICES.md.
        // Verified against the author's VPM archive:
        // https://gitlab.com/techanon/videoplayershim/-/releases/v1.5.0/downloads/package/ArchiTech.VideoPlayerShim_v1.5.0.zip
        // Archive SHA-256: c36eb51d5f8b9476d77c8561e7783af4f04a8e2425d9ef5977d239da5c8b454d
        private const string RepairedMethods = @"        static void ResolveURLCallback(VRCUrl url, int resolution, UnityEngine.Object videoPlayer, Action<string> urlResolvedCallback, Action<VideoError> errorCallback)
        {
            int e = SessionState.GetInt(forceVideoErrorKey, -1);
            if (e > -1)
            {
                errorCallback.Invoke((VideoError)e);
                return;
            }

            string originalUrl = url.ToString();
            // Custom player protocols such as rtspt:// are already resolved and must be passed directly to the player.
            if (!originalUrl.StartsWith(""https://"", StringComparison.OrdinalIgnoreCase))
            {
                urlResolvedCallback(originalUrl);
                return;
            }

            System.Diagnostics.Process ytdlProcess = new System.Diagnostics.Process();
            ytdlProcess.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            ytdlProcess.StartInfo.CreateNoWindow = true;
            ytdlProcess.StartInfo.UseShellExecute = false;
            ytdlProcess.StartInfo.RedirectStandardOutput = true;
            ytdlProcess.StartInfo.RedirectStandardError = true;
            ytdlProcess.StartInfo.FileName = youtubeDLPath;
            ytdlProcess.StartInfo.Arguments = $""--no-check-certificate --no-cache-dir --rm-cache-dir -f \""mp4[height<=?{resolution}]/best[height<=?{resolution}]\"" --get-url \""{url}\"""";

            Debug.Log($""[<color=#9C6994>Video Playback</color>] Attempting to resolve URL '{url}'"");

            ytdlProcess.Start();
            runningYTDLProcesses.Add(ytdlProcess);

            ((MonoBehaviour)videoPlayer).StartCoroutine(URLResolveCoroutine(originalUrl, ytdlProcess, urlResolvedCallback, errorCallback));

            registeredBehaviours.Add((MonoBehaviour)videoPlayer);
        }

        private const string ErrorText = ""ERROR:"";

        static IEnumerator URLResolveCoroutine(string originalUrl, System.Diagnostics.Process ytdlProcess, Action<string> urlResolvedCallback, Action<VideoError> errorCallback)
        {
            while (!ytdlProcess.HasExited)
                yield return new WaitForSeconds(0.1f);

            runningYTDLProcesses.Remove(ytdlProcess);

            // STDOUT handles the resulting URL
            var stdout = ytdlProcess.StandardOutput;
            // STDERR handles any failure messages
            var stderr = ytdlProcess.StandardError;
            string ytdlError = null;
            string lastStderrLine = null;
            while (!stderr.EndOfStream)
            {
                string line = stderr.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                lastStderrLine = line;
                if (line.StartsWith(ErrorText, StringComparison.OrdinalIgnoreCase))
                    ytdlError = line.Substring(ErrorText.Length).TrimStart();
            }

            if (ytdlProcess.ExitCode != 0 || ytdlError != null)
            {
                string errorMessage = ytdlError ?? lastStderrLine ?? $""YTDL exited with code {ytdlProcess.ExitCode}."";
                Debug.LogError($""[<color=#9C6994>Video Playback</color>] {errorMessage}"");
                errorCallback(VideoError.PlayerError);
                yield break;
            }

            string resolvedUrl = null;
            while (!stdout.EndOfStream)
            {
                string line = stdout.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith(""https://"", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(""http://"", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedUrl = line;
                    break;
                }
            }

            // Valid URL was found
            if (resolvedUrl != null)
            {
                Debug.Log($""[<color=#9C6994>Video Playback</color>] URL '{originalUrl}' resolved to '{resolvedUrl}'"");
                urlResolvedCallback(resolvedUrl);
            }
            else
            {
                // this usually shouldn't be reached but just in case...
                Debug.LogError($""[<color=#9C6994>Video Playback</color>] Failed to resolve URL '{originalUrl}'. No error detected."");
                errorCallback(VideoError.InvalidURL);
            }
        }
    }
}
";
    }
}
