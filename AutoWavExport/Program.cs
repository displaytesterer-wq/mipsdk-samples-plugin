using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace AutoWavExport
{
    public sealed class Settings
    {
        public string ManagementServer { get; set; }
        public string Username { get; set; }
        public string PasswordFile { get; set; }
        public string OutputRoot { get; set; }
        public int SampleRate { get; set; }
        public string Codec { get; set; }
        public int ExportTimeoutMinutes { get; set; }
        public int Retries { get; set; }
        public bool DryRun { get; set; }
    }

    internal static class Program
    {
        static readonly Guid IntegrationId = new Guid("86F42E94-41B8-4F47-9E56-6A815A0C482F");
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static int Main(string[] args)
        {
            using (var mutex = new Mutex(false, @"Local\MilestoneAutoWavExport"))
            {
                bool locked = false;
                try
                {
                    locked = mutex.WaitOne(TimeSpan.FromSeconds(1));
                    if (!locked) { Console.Error.WriteLine("Another export is active."); return 2; }
                    return Run(args);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        static int Run(string[] args)
        {
            string config = Opt(args, "--config") ?? "settings.json";
            var s = Json.Deserialize<Settings>(File.ReadAllText(config, Encoding.UTF8));
            if (s == null || string.IsNullOrWhiteSpace(s.ManagementServer) ||
                string.IsNullOrWhiteSpace(s.OutputRoot) || string.IsNullOrWhiteSpace(s.PasswordFile))
                throw new InvalidOperationException("Incomplete settings.json");
            Directory.CreateDirectory(s.OutputRoot);
            string logDirectory = Path.Combine(s.OutputRoot, "_logs");
            Directory.CreateDirectory(logDirectory);
            string logFile = Path.Combine(logDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            Action<string> log = message =>
            {
                var line = DateTime.Now.ToString("o") + " " + message;
                Console.WriteLine(line);
                File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
            };

            DateTime end = (DateTime.Now.Date.AddHours((DateTime.Now.Hour / 6) * 6));
            var specified = Opt(args, "--end");
            if (!string.IsNullOrWhiteSpace(specified))
                end = DateTime.ParseExact(specified, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            if (end.Minute != 0 || end.Second != 0 || end.Hour % 6 != 0 || end > DateTime.Now)
                throw new ArgumentException("End must be a completed local 6-hour boundary.");
            DateTime start = end.AddHours(-6);
            Uri url = new Uri(s.ManagementServer);
            if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Use verified HTTPS Management Server URL.");
            string password = ReadDpapiPassword(s.PasswordFile);
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            try
            {
                var cc = VideoOS.Platform.Login.Util.BuildCredentialCache(url, s.Username ?? "exporter", password, "Basic");
                VideoOS.Platform.SDK.Environment.AddServer(true, url, cc, true);
                VideoOS.Platform.SDK.Environment.Login(url, IntegrationId, "Auto WAV Export", "0.1.0", "Internal", true);
                var roots = Configuration.Instance.GetItemsByKind(Kind.Microphone, ItemHierarchy.SystemDefined);
                var devices = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots) Collect(root, devices, new HashSet<string>(), 0);
                log("Inventory count: " + devices.Count);
                WriteInventory(s.OutputRoot, devices);
                var history = UpdateNameHistory(s.OutputRoot, devices, DateTime.Now, log);
                bool inventory = args.Contains("--inventory");
                string selected = Opt(args, "--device");
                
                foreach (var pair in devices.OrderBy(d => d.Key))
                    log("MIC " + pair.Key + " name=" + pair.Value.Name + " folder=" +
                        FolderName(pair.Value, pair.Key));
                if (inventory) return 0;
                if (devices.Count == 0) { log("ERROR no microphones discovered"); return 3; }
                int failures = 0;
                int attempted = 0;
                foreach (var pair in devices.OrderBy(x => x.Key))
                {
                    if (selected != null && !string.Equals(selected, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    attempted++;
                    try
                    {
                        string folder = Path.Combine(s.OutputRoot, FolderForWindow(pair.Value, pair.Key, end, history));
                        folder = Path.Combine(folder, start.ToString("yyyy-MM-dd"));
                        Directory.CreateDirectory(folder);
                        string filename = start.ToString("HHmm") + "-" + end.ToString("HHmm") + ".wav";
                        string finalPath = Path.Combine(folder, filename);
                        if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 44)
                        { log("SKIP existing " + finalPath); continue; }
                        if (s.DryRun) { log("DRY RUN " + pair.Key + " -> " + finalPath); continue; }
                        int tries = Math.Max(1, s.Retries + 1);
                        bool success = false;
                        for (int n = 1; n <= tries && !success; n++)
                        {
                            try
                            {
                                ExportOne(pair.Value, folder, filename, start, end, s, log);
                                success = true;
                                log("SUCCESS " + pair.Key + " " + finalPath);
                            }
                            catch (Exception ex)
                            {
                                log("ATTEMPT " + n + "/" + tries + " FAILED " + pair.Key + ": " + ex.Message);
                                if (n < tries) Thread.Sleep(TimeSpan.FromSeconds(20 * n));
                                else throw;
                            }
                        }
                    }
                    catch (Exception ex) { failures++; log("ERROR " + pair.Key + ": " + ex); }
                }
                if (attempted == 0) { log("No matching microphone selected."); return 4; }
                log("Finished: attempted=" + attempted + " failures=" + failures);
                return failures == 0 ? 0 : 5;
            }
            finally { VideoOS.Platform.SDK.Environment.RemoveAllServers(); }
        }

        static void Collect(Item item, IDictionary<string,Item> list, ISet<string> visited, int depth)
        {
            if (item == null || depth > 15 || item.FQID == null) return;
            string key = item.FQID.ToString();
            if (!visited.Add(key)) return;
            if (item.FQID.Kind == Kind.Microphone && item.FQID.FolderType == FolderType.No)
                list[item.FQID.ObjectId.ToString("D")] = item;
            foreach (var child in item.GetChildren() ?? new List<Item>())
                Collect(child, list, visited, depth + 1);
        }

        static void ExportOne(Item mic, string folder, string name, DateTime start, DateTime end, Settings s, Action<string> log)
        {
            // Request one microphone per WAV; no related camera is necessary.
            // UTC follows the official standalone AudioExportSample.
            string tempName = Path.GetFileNameWithoutExtension(name) + ".working-" + Guid.NewGuid().ToString("N") + ".wav";
            string tempPath = Path.Combine(folder, tempName);
            WAVExporter exporter = null;
            try
            {
                exporter = new WAVExporter
                {
                    FileName = tempName,
                    Path = folder,
                    AudioList = new List<Item> { mic },
                    AudioSampleRate = s.SampleRate <= 0 ? 8000 : s.SampleRate
                };
                exporter.Init();
                var codecs = exporter.CodecList;
                string codec = string.IsNullOrWhiteSpace(s.Codec) ? codecs.FirstOrDefault() : s.Codec;
                if (string.IsNullOrWhiteSpace(codec) || !codecs.Contains(codec))
                    throw new InvalidOperationException("Configured WAV codec is unavailable.");
                exporter.Codec = codec;
                if (!exporter.StartExport(start.ToUniversalTime(), end.ToUniversalTime()))
                    throw new InvalidOperationException("StartExport failed: " + exporter.LastErrorString);
                DateTime deadline = DateTime.UtcNow.AddMinutes(Math.Max(5, s.ExportTimeoutMinutes));
                while (exporter.Progress < 100)
                {
                    if (exporter.LastError > 0)
                        throw new InvalidOperationException(exporter.LastErrorString);
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("WAV exporter exceeded timeout.");
                    Thread.Sleep(1000);
                }
                exporter.EndExport();
                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length <= 44)
                    throw new IOException("WAV output missing or empty: " + tempPath);
                File.Move(tempPath, Path.Combine(folder, name));
            }
            catch
            {
                if (exporter != null) { try { exporter.Cancel(); } catch {} }
                throw;
            }
            finally
            {
                if (exporter != null)
                {
                    try { exporter.EndExport(); } catch {}
                    try { exporter.Close(); } catch {}
                }
                if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch {} }
            }
        }

        // Append-only observation history. A timestamp is when the exporter detected a
        // name, NOT necessarily when a Milestone administrator actually renamed it.
        sealed class NameObservation
        {
            public string DeviceId { get; set; }
            public DateTime ObservedAt { get; set; }
            public string MicrophoneName { get; set; }
            public string Folder { get; set; }
        }

        static List<NameObservation> UpdateNameHistory(
            string root, IDictionary<string, Item> devices, DateTime now, Action<string> log)
        {
            string path = Path.Combine(root, "device_history.csv");
            var history = new List<NameObservation>();
            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8).Skip(1))
                {
                    try
                    {
                        var cells = ParseCsv(line);
                        if (cells.Count != 4) continue;
                        history.Add(new NameObservation {
                            ObservedAt = DateTime.ParseExact(cells[0], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                            DeviceId = cells[1], MicrophoneName = cells[2], Folder = cells[3]
                        });
                    }
                    catch (Exception ex) { throw new InvalidDataException("Invalid device_history.csv; preserve and repair manually.", ex); }
                }
            }
            if (!File.Exists(path))
                File.WriteAllText(path, "ObservedAt,DeviceId,MicrophoneName,Folder" + Environment.NewLine, Encoding.UTF8);

            foreach (var pair in devices.OrderBy(x => x.Key))
            {
                string folder = FolderName(pair.Value, pair.Key);
                var last = history.Where(x => x.DeviceId == pair.Key).OrderByDescending(x => x.ObservedAt).FirstOrDefault();
                if (last != null && last.MicrophoneName == pair.Value.Name && last.Folder == folder) continue;
                var entry = new NameObservation { ObservedAt = now, DeviceId = pair.Key,
                    MicrophoneName = pair.Value.Name, Folder = folder };
                string row = Csv(now.ToString("o")) + "," + Csv(pair.Key) + "," +
                             Csv(pair.Value.Name) + "," + Csv(folder) + Environment.NewLine;
                File.AppendAllText(path, row, Encoding.UTF8);
                history.Add(entry);
                log((last == null ? "NEW MIC " : "NAME CHANGE OBSERVED ") + pair.Key + ": " + folder);
            }
            return history;
        }

        static string FolderForWindow(Item mic, string deviceId, DateTime end, List<NameObservation> history)
        {
            // Use the name last observed BEFORE the recording interval ended.
            // A name first observed after this period cannot safely label past audio.
            var match = history.Where(x => x.DeviceId == deviceId && x.ObservedAt <= end)
                               .OrderByDescending(x => x.ObservedAt).FirstOrDefault();
            if (match != null) return match.Folder;
            // For recordings earlier than the first observation, historical name is UNKNOWN.
            var first = history.Where(x => x.DeviceId == deviceId)
                               .OrderBy(x => x.ObservedAt).FirstOrDefault();
            if (first != null && end >= first.ObservedAt.AddHours(-2)) return first.Folder;
            return "MIC_" + deviceId + "_historical_unknown";
        }

        static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

        static List<string> ParseCsv(string input)
        {
            var fields = new List<string>();
            var part = new StringBuilder();
            bool quoted = false;
            for(int i=0; i<input.Length; i++)
            {
                char ch = input[i];
                if(ch == '"')
                {
                    if(quoted && i+1<input.Length && input[i+1]=='"') { part.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if(ch==',' && !quoted) { fields.Add(part.ToString()); part.Clear(); }
                else part.Append(ch);
            }
            if(quoted) throw new FormatException("Unclosed CSV field");
            fields.Add(part.ToString());
            return fields;
        }

        static void WriteInventory(string root, IDictionary<string, Item> devices)
        {
            Func<string,string> csv = value => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
            var rows = new List<string> { "DeviceId,MicrophoneName,Folder" };
            foreach (var pair in devices.OrderBy(x => x.Key))
                rows.Add(csv(pair.Key) + "," + csv(pair.Value.Name) + "," + csv(FolderName(pair.Value, pair.Key)));
            File.WriteAllLines(Path.Combine(root, "microphones.csv"), rows, Encoding.UTF8);
        }

        static string FolderName(Item mic, string id)
        {
            return SafeName(mic.Name) + "__" + id;
        }

        static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
            if (clean.Length > 90) clean = clean.Substring(0, 90);
            if (string.IsNullOrWhiteSpace(clean) || clean == "..") throw new ArgumentException("Invalid hardware name");
            return clean;
        }
        static string Opt(string[] a, string k)
        {
            for(int i=0; i<a.Length-1; i++) if (a[i] == k) return a[i+1];
            return null;
        }
        static string ReadDpapiPassword(string file)
        {
            // PowerShell ConvertFrom-SecureString without -Key creates a DPAPI encrypted standard string.
            // The file is decrypted using the current user's DPAPI key.
            // PowerShell exposes DPAPI blob as Unicode text encrypted bytes in hexadecimal.
            var hex = File.ReadAllText(file).Trim();
            var bytes = Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i*2, 2), 16)).ToArray();
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.Unicode.GetString(plain);
        }
    }
}
