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
        // "Windows" (анхдагч): task ажиллуулж буй Windows account-аар нэвтэрнэ. http:// болон https:// хоёулаа болно,
        //   нууц үгийн файл шаардлагагүй.
        // "Basic": XProtect Basic user (Username + PasswordFile). Нууц үг илгээгддэг тул зөвхөн https://.
        public string AuthMode { get; set; }
        public string Username { get; set; }
        public string PasswordFile { get; set; }
        public string OutputRoot { get; set; }
        // Лог, төлөв, нэрийн түүх, microphones.csv хадгалах хавтас. Хоосон бол settings.json-ий
        // хажуугийн "data" хавтас (жишээ нь C:\MilestoneAutoWav\data). OutputRoot-д зөвхөн WAV үлдэнэ.
        public string DataRoot { get; set; }
        public int SampleRate { get; set; }
        public string Codec { get; set; }
        public int ExportTimeoutMinutes { get; set; }
        public int Retries { get; set; }
        public bool DryRun { get; set; }
        public int BackfillDays { get; set; }
        public int MaxIntervalsPerRun { get; set; }
        public int MinimumFreeGB { get; set; }
        public int RetentionDays { get; set; }
        public bool EnableRetentionDelete { get; set; }
        // Нэг удаагийн ажлын нийт хугацааны хязгаар (минут). Task Scheduler-ийн
        // ExecutionTimeLimit-ээс (Install-Task.ps1-д 5 цаг) бага байх ёстой, тэгэхгүй бол процесс хүчээр зогсоогдоно.
        public int MaxRunMinutes { get; set; }
        // Нэг микрофоны нэг цонх энэ тооны удаа бүтэлгүйтвэл автоматаар дахин оролдохоо болино
        // (ихэвчлэн тухайн хугацаанд бичлэг байхгүй үед). Гараар --end өгвөл энэ хязгаарыг үл тооно.
        public int MaxWindowAttempts { get; set; }
    }

    // Цонхны буруу биш алдаанууд: MaxWindowAttempts-д тоологдохгүй.
    sealed class LowDiskException : IOException { public LowDiskException(string m) : base(m) { } }
    sealed class RunBudgetExceededException : TimeoutException { public RunBudgetExceededException(string m) : base(m) { } }

    internal static class Program
    {
        static readonly Guid IntegrationId = new Guid("86F42E94-41B8-4F47-9E56-6A815A0C482F");
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // Global\ ашигласнаар хуваарьт ажил (session 0) болон гараар ажиллуулсан (RDP/console session)
        // ажил нэг түгжээг хуваалцаж, хэзээ ч зэрэг export хийхгүй.
        const string MutexName = @"Global\MilestoneAutoWavExport";
        // Install-Task.ps1-д тохируулсан Task Scheduler-ийн ExecutionTimeLimit (минут).
        const int TaskSchedulerLimitMinutes = 300;
        const string WorkingMarker = ".working-";

        static int Main(string[] args)
        {
            Mutex mutex = null;
            bool locked = false;
            try
            {
                try { mutex = new Mutex(false, MutexName); }
                catch (UnauthorizedAccessException)
                {
                    // Түгжээ байгаа бөгөөд өөр Windows account одоо ажиллаж байна.
                    Console.Error.WriteLine("Another export is active (lock held by another account).");
                    return 2;
                }
                try { locked = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException)
                {
                    // Өмнөх процесс түгжээтэй байхдаа таслагдсан; одоо түгжээ манайх.
                    locked = true;
                    Console.Error.WriteLine("Previous run ended abnormally; lock recovered.");
                }
                if (!locked) { Console.Error.WriteLine("Another export is active."); return 2; }
                return Run(args);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                if (locked) { try { mutex.ReleaseMutex(); } catch { } }
                if (mutex != null) mutex.Dispose();
            }
        }

        static int Run(string[] args)
        {
            DateTime runStartedUtc = DateTime.UtcNow;
            string config = Opt(args, "--config") ?? "settings.json";
            var s = Json.Deserialize<Settings>(File.ReadAllText(config, Encoding.UTF8));
            if (s == null || string.IsNullOrWhiteSpace(s.ManagementServer) ||
                string.IsNullOrWhiteSpace(s.OutputRoot))
                throw new InvalidOperationException("Incomplete settings.json");
            bool basicAuth = string.Equals(s.AuthMode, "Basic", StringComparison.OrdinalIgnoreCase);
            if (!basicAuth && !string.IsNullOrWhiteSpace(s.AuthMode) && !string.Equals(s.AuthMode, "Windows", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AuthMode must be \"Windows\" or \"Basic\".");
            if (basicAuth && string.IsNullOrWhiteSpace(s.PasswordFile))
                throw new InvalidOperationException("AuthMode Basic requires PasswordFile.");
            ApplyDefaultsAndValidate(s);

            Directory.CreateDirectory(s.OutputRoot);
            string dataRoot = string.IsNullOrWhiteSpace(s.DataRoot)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config)), "data")
                : s.DataRoot;
            string logDirectory = Path.Combine(dataRoot, "logs");
            string stateDirectory = Path.Combine(dataRoot, "state");
            Directory.CreateDirectory(logDirectory);
            Directory.CreateDirectory(stateDirectory);
            // Өмнөх хувилбар эдгээр файлыг OutputRoot-д бичдэг байсан; нэг удаа шилжүүлнэ.
            var migrated = MigrateMetadata(s.OutputRoot, dataRoot, logDirectory, stateDirectory);
            string logFile = Path.Combine(logDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            Action<string> log = message =>
            {
                var line = DateTime.Now.ToString("o") + " " + message;
                Console.WriteLine(line);
                File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
            };
            log("DataRoot=" + dataRoot);
            foreach (var m in migrated) log("MIGRATED " + m);

            DateTime now = DateTime.Now;
            DateTime end = now.Date.AddHours((now.Hour / 6) * 6);
            var specified = Opt(args, "--end");
            if (!string.IsNullOrWhiteSpace(specified))
                end = DateTime.ParseExact(specified, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            if (end.Minute != 0 || end.Second != 0 || end.Hour % 6 != 0 || end > now)
                throw new ArgumentException("End must be a completed local 6-hour boundary.");
            Uri url = new Uri(s.ManagementServer);
            bool https = url.Scheme == Uri.UriSchemeHttps;
            if (!https && url.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("ManagementServer must start with http:// or https://");
            // Basic user-ийн нууц үгийг шифрлэлтгүй холболтоор илгээхгүй.
            if (basicAuth && !https)
                throw new InvalidOperationException("AuthMode Basic requires an https:// Management Server URL. Use AuthMode Windows for http://.");

            // Глобал түгжээ манайд байгаа тул *.working-* файл бүр таслагдсан ажлаас үлдсэн хог.
            CleanupStaleWorkingFiles(s.OutputRoot, s.DryRun, log);

            DateTime runDeadlineUtc = runStartedUtc.AddMinutes(s.MaxRunMinutes);
            CredentialCache cc;
            if (basicAuth)
            {
                string password = ReadDpapiPassword(s.PasswordFile);
                cc = VideoOS.Platform.Login.Util.BuildCredentialCache(url, s.Username ?? "exporter", password, "Basic");
            }
            else
            {
                // Task ажиллуулж буй Windows account-ын мэдээллээр Kerberos/NTLM-ээр нэвтэрнэ.
                // Нууц үг програмд дамжихгүй, Windows өөрөө баталгаажуулна.
                cc = new CredentialCache();
                cc.Add(url, "Negotiate", CredentialCache.DefaultNetworkCredentials);
            }
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            try
            {
                log("Login " + url + " as " + (basicAuth ? "Basic user " + (s.Username ?? "exporter")
                                                          : "Windows user " + Environment.UserDomainName + "\\" + Environment.UserName));
                // Эхний параметр secureOnly: http:// хаягтай үед false байх ёстой.
                VideoOS.Platform.SDK.Environment.AddServer(https, url, cc, true);
                VideoOS.Platform.SDK.Environment.Login(url, IntegrationId, "Auto WAV Export", "0.2.0", "Internal", true);
                var roots = Configuration.Instance.GetItemsByKind(Kind.Microphone, ItemHierarchy.SystemDefined);
                var devices = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots) Collect(root, devices, new HashSet<string>(), 0);
                log("Inventory count: " + devices.Count);
                WriteInventory(dataRoot, devices);
                var history = UpdateNameHistory(dataRoot, devices, DateTime.Now, log);
                bool inventory = args.Contains("--inventory");
                string selected = Opt(args, "--device");

                foreach (var pair in devices.OrderBy(d => d.Key))
                    log("MIC " + pair.Key + " name=" + pair.Value.Name + " folder=" +
                        FolderName(pair.Value, pair.Key));
                if (inventory) return 0;
                if (devices.Count == 0) { log("ERROR no microphones discovered"); return 3; }

                var targets = devices
                    .Where(d => selected == null || string.Equals(selected, d.Key, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.Key)
                    .ToList();
                if (targets.Count == 0) { log("No matching microphone selected."); return 4; }

                // Хамгийн шинэ цонхыг эхэлж, бүх микрофонд хийсний дараа хуучин цонх руу шилжинэ.
                // Ингэснээр эхний микрофонуудын хуримтлал сүүлийнхийг хойшлуулахгүй.
                var windows = new List<DateTime>();
                if (specified != null) windows.Add(end);
                else for (DateTime w = end; w > end.AddDays(-s.BackfillDays); w = w.AddHours(-6)) windows.Add(w);

                var attempts = AttemptStore.Load(Path.Combine(stateDirectory, "window_attempts.csv"));
                attempts.Prune(end.AddDays(-s.BackfillDays - 1));

                // Тайланг мөр бүрээр нэмж бичдэг тул процесс таслагдсан ч хадгалагдана.
                string reportPath = Path.Combine(logDirectory, "export-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                File.WriteAllText(reportPath, "device_id,window_start,window_end,status,detail" + Environment.NewLine, Encoding.UTF8);
                log("Report=" + reportPath);

                int failures = 0, deferred = 0, gaveUp = 0, jobsProcessed = 0;
                foreach (var windowEnd in windows)
                {
                    foreach (var pair in targets)
                    {
                        DateTime windowStart = windowEnd.AddHours(-6);
                        string status = "ERROR", detail = "";
                        try
                        {
                            string folder = Path.Combine(s.OutputRoot, FolderForWindow(pair.Value, pair.Key, windowEnd, history));
                            folder = Path.Combine(folder, windowStart.ToString("yyyy-MM-dd"));
                            string filename = windowStart.ToString("HHmm") + "-" + windowEnd.ToString("HHmm") + ".wav";
                            string finalPath = Path.Combine(folder, filename);
                            if (File.Exists(finalPath) && WavCheck.Validate(finalPath, out detail))
                            { status = "EXISTS_VALID"; attempts.Clear(pair.Key, windowEnd); continue; }
                            if (File.Exists(finalPath)) throw new IOException("Existing WAV invalid, manual quarantine required: " + detail);

                            int previous = attempts.Count(pair.Key, windowEnd);
                            if (specified == null && previous >= s.MaxWindowAttempts)
                            {
                                status = "GAVE_UP";
                                detail = previous + " failed runs; last error: " + attempts.LastError(pair.Key, windowEnd) +
                                         ". Rerun manually with --end " + windowEnd.ToString("yyyy-MM-ddTHH:mm:ss") + " --device " + pair.Key;
                                gaveUp++;
                                continue;
                            }
                            if (jobsProcessed >= s.MaxIntervalsPerRun) { status = "DEFERRED"; detail = "MaxIntervalsPerRun reached"; deferred++; continue; }
                            // Дор хаяж нэг оролдлого болон хаалтад хугацаа үлдсэн байх ёстой.
                            if (DateTime.UtcNow.AddMinutes(5) >= runDeadlineUtc) { status = "DEFERRED"; detail = "MaxRunMinutes budget reached"; deferred++; continue; }
                            if (s.DryRun) { status = "DRY_RUN"; log("DRY RUN " + finalPath); continue; }
                            GuardDisk(s.OutputRoot, s.MinimumFreeGB);
                            Directory.CreateDirectory(folder);
                            jobsProcessed++;
                            int tries = Math.Max(1, s.Retries + 1);
                            for (int n = 1; n <= tries; n++)
                            {
                                try
                                {
                                    DateTime attemptDeadline = DateTime.UtcNow.AddMinutes(s.ExportTimeoutMinutes);
                                    ExportOne(pair.Value, folder, filename, windowStart, windowEnd, s, attemptDeadline, runDeadlineUtc);
                                    status = "SUCCESS"; detail = "WAV structure valid";
                                    attempts.Clear(pair.Key, windowEnd);
                                    log("SUCCESS " + pair.Key + " " + finalPath);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    detail = ex.Message;
                                    log("ATTEMPT " + n + "/" + tries + " FAILED " + pair.Key + " " + windowEnd.ToString("o") + ": " + detail);
                                    TimeSpan pause = TimeSpan.FromSeconds(20 * n);
                                    bool timeLeft = DateTime.UtcNow.Add(pause).AddMinutes(5) < runDeadlineUtc;
                                    if (n == tries || !timeLeft || ex is RunBudgetExceededException) throw;
                                    Thread.Sleep(pause);
                                }
                            }
                        }
                        catch (RunBudgetExceededException ex)
                        {
                            // Task Scheduler-ийн хязгаарт багтаахаар зогсоосон; дараагийн удаа дахин хийнэ, тоологдохгүй.
                            status = "DEFERRED"; detail = ex.Message; deferred++;
                            log("DEFERRED " + pair.Key + " " + windowEnd.ToString("o") + ": " + ex.Message);
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            detail = ex.Message;
                            log("ERROR " + pair.Key + ": " + ex);
                            if (!s.DryRun && !(ex is LowDiskException)) attempts.RecordFailure(pair.Key, windowEnd, ex.Message);
                        }
                        finally
                        {
                            File.AppendAllText(reportPath,
                                Csv(pair.Key) + "," + Csv(windowStart.ToString("o")) + "," +
                                Csv(windowEnd.ToString("o")) + "," + Csv(status) + "," + Csv(detail) + Environment.NewLine,
                                Encoding.UTF8);
                        }
                    }
                }
                if (!s.DryRun) attempts.Save();
                log("Done failures=" + failures + " deferred=" + deferred + " gave_up=" + gaveUp);
                if (!s.DryRun && s.EnableRetentionDelete) RetentionCleanup(s.OutputRoot, s.RetentionDays, log);
                // 0-ээс ялгаатай exit code-ийг Task Scheduler болон гадны хяналт хүлээн авна.
                // GAVE_UP цонхууд орхигдсон тэр удаадаа алдаа гэж бүртгэгдсэн.
                return failures == 0 && deferred == 0 ? 0 : 5;
            }
            finally { VideoOS.Platform.SDK.Environment.RemoveAllServers(); }
        }

        static void ApplyDefaultsAndValidate(Settings s)
        {
            // JavaScriptSerializer байхгүй тоог 0 болгодог; 0-ийг "анхдагч утга" гэж үзнэ.
            if (s.ExportTimeoutMinutes <= 0) s.ExportTimeoutMinutes = 60;
            if (s.MaxRunMinutes <= 0) s.MaxRunMinutes = 270;
            if (s.MaxWindowAttempts <= 0) s.MaxWindowAttempts = 3;
            if (s.Retries < 0) s.Retries = 0;

            if (s.BackfillDays <= 0 || s.BackfillDays > 90 || s.MaxIntervalsPerRun <= 0 || s.MaxIntervalsPerRun > 100)
                throw new InvalidOperationException("Set BackfillDays 1..90 and MaxIntervalsPerRun 1..100.");
            if (s.MinimumFreeGB <= 0) throw new InvalidOperationException("MinimumFreeGB must be positive.");
            if (s.MaxRunMinutes < 30 || s.MaxRunMinutes > TaskSchedulerLimitMinutes - 15)
                throw new InvalidOperationException("MaxRunMinutes must be 30.." + (TaskSchedulerLimitMinutes - 15) +
                                                    " (Task Scheduler stops the task at " + TaskSchedulerLimitMinutes + " minutes).");
            if (s.ExportTimeoutMinutes < 5 || s.ExportTimeoutMinutes > s.MaxRunMinutes - 5)
                throw new InvalidOperationException("ExportTimeoutMinutes must be 5..MaxRunMinutes-5.");
            if (s.MaxWindowAttempts > 20) throw new InvalidOperationException("MaxWindowAttempts must be 1..20.");
            if (s.EnableRetentionDelete && s.RetentionDays <= s.BackfillDays)
                throw new InvalidOperationException("RetentionDays must be greater than BackfillDays, otherwise deleted files are exported again.");
        }

        // OutputRoot\_logs, _state, device_history.csv, microphones.csv-г DataRoot руу зөөнө.
        // Зорьсон газар файл байвал хуучныг нь хөндөхгүй. C: болон D: өөр диск байж болох тул хуулаад устгана.
        static List<string> MigrateMetadata(string outputRoot, string dataRoot, string logDirectory, string stateDirectory)
        {
            var moved = new List<string>();
            if (string.Equals(Path.GetFullPath(outputRoot).TrimEnd('\\'), Path.GetFullPath(dataRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return moved;
            Action<string, string> move = (from, to) =>
            {
                if (!File.Exists(from) || File.Exists(to)) return;
                File.Copy(from, to);
                File.Delete(from);
                moved.Add(from + " -> " + to);
            };
            move(Path.Combine(outputRoot, "device_history.csv"), Path.Combine(dataRoot, "device_history.csv"));
            move(Path.Combine(outputRoot, "microphones.csv"), Path.Combine(dataRoot, "microphones.csv"));
            foreach (var pair in new[] { Tuple.Create("_logs", logDirectory), Tuple.Create("_state", stateDirectory) })
            {
                string oldDir = Path.Combine(outputRoot, pair.Item1);
                if (!Directory.Exists(oldDir)) continue;
                foreach (var f in Directory.GetFiles(oldDir))
                    move(f, Path.Combine(pair.Item2, Path.GetFileName(f)));
                try { if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir); } catch { }
            }
            return moved;
        }

        static void CleanupStaleWorkingFiles(string root, bool dryRun, Action<string> log)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*" + WorkingMarker + "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                if (rel.StartsWith("_", StringComparison.Ordinal)) continue;
                if (dryRun) { log("DRY RUN would remove stale temp " + file); continue; }
                try { File.Delete(file); log("REMOVED stale temp " + file); }
                catch (Exception ex) { log("WARN could not remove stale temp " + file + ": " + ex.Message); }
            }
        }

        static void GuardDisk(string folder, int requiredGb)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(folder));
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < (long)requiredGb * 1024 * 1024 * 1024)
                throw new LowDiskException("Low export disk free space: " + free + " bytes");
        }

        static void RetentionCleanup(string root, int days, Action<string> log)
        {
            if (days < 1) throw new InvalidOperationException("RetentionDays must be positive.");
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            DateTime cutoff = DateTime.Today.AddDays(-days);
            foreach (var micFolder in Directory.GetDirectories(root))
            {
                string dir = Path.GetFileName(micFolder);
                if (dir.StartsWith("_", StringComparison.Ordinal)) continue;
                foreach (var dayFolder in Directory.GetDirectories(micFolder))
                {
                    DateTime date;
                    if (!DateTime.TryParseExact(Path.GetFileName(dayFolder), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date >= cutoff) continue;
                    foreach (var file in Directory.GetFiles(dayFolder, "*.wav"))
                    {
                        if (!Path.GetFullPath(file).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Retention target outside root.");
                        File.Delete(file);
                        log("RETENTION_DELETE " + file);
                    }
                }
            }
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

        static void ExportOne(Item mic, string folder, string name, DateTime start, DateTime end, Settings s,
                              DateTime attemptDeadlineUtc, DateTime runDeadlineUtc)
        {
            // WAV файл бүрт нэг микрофон; холбоотой камер шаардлагагүй.
            // Албан ёсны AudioExportSample-ийн адил UTC цаг ашиглана.
            // FileName-ийг өргөтгөлгүй өгч, үүссэн файлыг дараа нь хайж олно. Ингэснээр SDK
            // ".wav"-ийг өөрөө нэмдэг эсэхээс үл хамааран ажиллана.
            string tempBase = Path.GetFileNameWithoutExtension(name) + WorkingMarker + Guid.NewGuid().ToString("N");
            WAVExporter exporter = null;
            bool ended = false;
            try
            {
                exporter = new WAVExporter
                {
                    FileName = tempBase,
                    Path = folder,
                    AudioList = new List<Item> { mic },
                    AudioSampleRate = s.SampleRate <= 0 ? 8000 : s.SampleRate
                };
                exporter.Init();
                var codecs = exporter.CodecList ?? new string[0];
                string codec = string.IsNullOrWhiteSpace(s.Codec) ? codecs.FirstOrDefault() : s.Codec;
                if (string.IsNullOrWhiteSpace(codec) || !codecs.Contains(codec))
                    throw new InvalidOperationException("Configured WAV codec is unavailable. Available: " + string.Join(", ", codecs));
                exporter.Codec = codec;
                if (!exporter.StartExport(start.ToUniversalTime(), end.ToUniversalTime()))
                    throw new InvalidOperationException("StartExport failed: " + exporter.LastErrorString);
                while (exporter.Progress < 100)
                {
                    if (exporter.LastError != 0)
                        throw new InvalidOperationException("Exporter error " + exporter.LastError + ": " + exporter.LastErrorString);
                    if (DateTime.UtcNow > runDeadlineUtc)
                        throw new RunBudgetExceededException("Stopped at MaxRunMinutes; window will be retried next run.");
                    if (DateTime.UtcNow > attemptDeadlineUtc)
                        throw new TimeoutException("WAV exporter exceeded ExportTimeoutMinutes.");
                    Thread.Sleep(1000);
                }
                if (exporter.LastError != 0)
                    throw new InvalidOperationException("Exporter error " + exporter.LastError + ": " + exporter.LastErrorString);
                exporter.EndExport();
                ended = true;

                string produced = FindProducedFile(folder, tempBase);
                string reason;
                if (!WavCheck.Validate(produced, out reason))
                    throw new IOException("WAV structure check failed: " + reason);
                File.Move(produced, Path.Combine(folder, name));
            }
            catch
            {
                if (exporter != null && !ended) { try { exporter.Cancel(); } catch {} }
                throw;
            }
            finally
            {
                if (exporter != null)
                {
                    if (!ended) { try { exporter.EndExport(); } catch {} }
                    try { exporter.Close(); } catch {}
                }
                // Exporter-ийн түр нэрээр үлдээсэн бүх зүйлийг (файл, хавтас) устгана.
                foreach (var leftover in SafeEnumerate(folder, tempBase + "*"))
                {
                    try
                    {
                        if (Directory.Exists(leftover)) Directory.Delete(leftover, true);
                        else File.Delete(leftover);
                    }
                    catch {}
                }
            }
        }

        static string FindProducedFile(string folder, string tempBase)
        {
            string withExt = Path.Combine(folder, tempBase + ".wav");
            if (File.Exists(withExt)) return withExt;
            string bare = Path.Combine(folder, tempBase);
            if (File.Exists(bare)) return bare;
            // Зарим exporter export-ийн нэрээр дэд хавтас үүсгэдэг; доош нь хайна.
            var found = Directory.GetFiles(folder, tempBase + "*", SearchOption.AllDirectories)
                                 .Concat(Directory.GetDirectories(folder, tempBase + "*")
                                                  .SelectMany(d => Directory.GetFiles(d, "*.wav", SearchOption.AllDirectories)))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                                 .ToList();
            if (found.Count == 1) return found[0];
            if (found.Count == 0) throw new FileNotFoundException("Exporter reported 100% but no output file named " + tempBase + "[.wav] was found in " + folder);
            throw new IOException("Exporter produced several WAV files for one microphone: " + string.Join("; ", found));
        }

        static IEnumerable<string> SafeEnumerate(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return new string[0];
                return Directory.GetFileSystemEntries(folder, pattern);
            }
            catch { return new string[0]; }
        }

        // (Микрофон, цонх) тус бүрийн бүтэлгүйтлийн тоолуур. Бичлэггүй цонхыг
        // хязгааргүй дахин оролдож, exit code-ийг байнга алдаатай болгохоос сэргийлнэ.
        sealed class AttemptStore
        {
            readonly string path;
            readonly Dictionary<string, Tuple<int, string>> items = new Dictionary<string, Tuple<int, string>>(StringComparer.OrdinalIgnoreCase);

            AttemptStore(string path) { this.path = path; }

            static string Key(string deviceId, DateTime windowEnd)
            {
                return deviceId + "|" + windowEnd.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }

            public static AttemptStore Load(string path)
            {
                var store = new AttemptStore(path);
                if (!File.Exists(path)) return store;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var c = ParseCsv(line);
                    int n;
                    if (c.Count != 4 || !int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                    store.items[c[0] + "|" + c[1]] = Tuple.Create(n, c[3]);
                }
                return store;
            }

            public int Count(string deviceId, DateTime windowEnd)
            {
                Tuple<int, string> v;
                return items.TryGetValue(Key(deviceId, windowEnd), out v) ? v.Item1 : 0;
            }

            public string LastError(string deviceId, DateTime windowEnd)
            {
                Tuple<int, string> v;
                return items.TryGetValue(Key(deviceId, windowEnd), out v) ? v.Item2 : "";
            }

            public void RecordFailure(string deviceId, DateTime windowEnd, string error)
            {
                string k = Key(deviceId, windowEnd);
                items[k] = Tuple.Create(Count(deviceId, windowEnd) + 1, error ?? "");
                Save(); // таслагдсан ч тоологдохоор шууд хадгална
            }

            public void Clear(string deviceId, DateTime windowEnd) { items.Remove(Key(deviceId, windowEnd)); }

            public void Prune(DateTime olderThan)
            {
                foreach (var k in items.Keys.ToList())
                {
                    DateTime w;
                    string stamp = k.Substring(k.IndexOf('|') + 1);
                    if (DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out w) && w < olderThan)
                        items.Remove(k);
                }
            }

            public void Save()
            {
                var rows = new List<string> { "DeviceId,WindowEnd,FailedRuns,LastError" };
                foreach (var kv in items.OrderBy(x => x.Key))
                {
                    int bar = kv.Key.IndexOf('|');
                    rows.Add(Csv(kv.Key.Substring(0, bar)) + "," + Csv(kv.Key.Substring(bar + 1)) + "," +
                             Csv(kv.Value.Item1.ToString(CultureInfo.InvariantCulture)) + "," +
                             Csv((kv.Value.Item2 ?? "").Replace("\r", " ").Replace("\n", " ")));
                }
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, rows, Encoding.UTF8);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
        }

        // Зөвхөн нэмэгддэг нэрийн түүх. Цаг нь exporter нэрийг илрүүлсэн мөч бөгөөд
        // Milestone админ нэрийг яг сольсон мөч биш байж болно.
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
            // Бичлэгийн цонх дуусахаас ӨМНӨ хамгийн сүүлд ажигласан нэрийг хэрэглэнэ.
            // Энэ хугацааны дараа анх ажигласан нэрээр өнгөрсөн бичлэгийг нэрлэх нь найдваргүй.
            var match = history.Where(x => x.DeviceId == deviceId && x.ObservedAt <= end)
                               .OrderByDescending(x => x.ObservedAt).FirstOrDefault();
            if (match != null) return match.Folder;
            // Анхны ажиглалтаас өмнөх бичлэгийн нэр ТОДОРХОЙГҮЙ.
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
            var rows = new List<string> { "DeviceId,MicrophoneName,Folder" };
            foreach (var pair in devices.OrderBy(x => x.Key))
                rows.Add(Csv(pair.Key) + "," + Csv(pair.Value.Name) + "," + Csv(FolderName(pair.Value, pair.Key)));
            File.WriteAllLines(Path.Combine(root, "microphones.csv"), rows, Encoding.UTF8);
        }

        static string FolderName(Item mic, string id)
        {
            return SafeName(mic.Name) + "__" + id;
        }

        // Exception шидэхгүй: хоосон эсвэл буруу нэр бүх ажлыг зогсоох ёсгүй.
        // FolderName-ийн төгсгөлийн бүтэн GUID хавтсыг ямар ч үед давхцуулахгүй.
        static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.').Trim();
            if (clean.Length > 90) clean = clean.Substring(0, 90).TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(clean)) clean = "MIC_unnamed";
            return clean;
        }

        static string Opt(string[] a, string k)
        {
            for(int i=0; i<a.Length-1; i++) if (a[i] == k) return a[i+1];
            return null;
        }

        static string ReadDpapiPassword(string file)
        {
            // Set-Password.ps1 нууц үгийн UTF-16 байтыг DPAPI(CurrentUser)-аар шифрлээд hex хэлбэрээр хадгалдаг.
            // Файлыг зөвхөн үүсгэсэн Windows account л тайлж чадна.
            var hex = File.ReadAllText(file).Trim();
            var bytes = Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i*2, 2), 16)).ToArray();
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.Unicode.GetString(plain);
        }
    }
}
