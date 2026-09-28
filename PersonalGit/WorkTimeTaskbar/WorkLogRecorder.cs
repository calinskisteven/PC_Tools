using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WorkTimeTaskbar
{
    // Each continuously tracked project interval is checkpointed, then exported to
    // easy-to-open weekly CSVs. There is no dependence on resetting the main timer.
    internal sealed class WorkLogRecorder
    {
        private sealed class Interval
        {
            public Interval() { }
            public string Project { get; set; } = "Unassigned";
            public DateTime StartUtc { get; set; }
            public DateTime EndUtc { get; set; }
        }

        private sealed class Journal
        {
            public Journal() { }
            public List<Interval> Completed { get; set; } = new List<Interval>();
            public Interval Live { get; set; }
        }

        private sealed class WeeklyPart
        {
            public string Project;
            public DateTime StartUtc;
            public DateTime EndUtc;
        }

        public const string ExportFolder = @"C:\dev\WorkLog";
        private readonly string journalPath;
        private readonly Journal journal;
        private readonly Dictionary<string, string> lastExportText = new Dictionary<string, string>();
        public string LastError { get; private set; } = "";

        public WorkLogRecorder(string appDataFolder)
        {
            journalPath = Path.Combine(appDataFolder, "worklog-journal.json");
            // On corruption, fail closed: never replace an existing journal with a
            // blank one, since that could destroy previously recorded work.
            journal = File.Exists(journalPath)
                ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath))
                    ?? throw new InvalidDataException("Empty work log journal")
                : new Journal();
            if (journal.Completed == null) journal.Completed = new List<Interval>();
            // The app always relaunches paused. A previously running interval ends
            // at its last saved checkpoint, not at relaunch time.
            if (journal.Live != null)
            {
                if (journal.Live.EndUtc > journal.Live.StartUtc)
                    journal.Completed.Add(journal.Live);
                journal.Live = null;
            }
            TrySaveAndExport();
        }

        public void Start(string project)
        {
            if (journal.Live != null) Finish();
            DateTime now = DateTime.UtcNow;
            journal.Live = new Interval
            {
                Project = string.IsNullOrWhiteSpace(project) ? "Unassigned" : project.Trim(),
                StartUtc = now,
                EndUtc = now
            };
            TrySaveAndExport();
        }

        public void SwitchProject(string project)
        {
            if (journal.Live == null) return;
            string next = string.IsNullOrWhiteSpace(project) ? "Unassigned" : project.Trim();
            if (string.Equals(next, journal.Live.Project, StringComparison.Ordinal)) return;
            Finish();
            Start(next);
        }

        public void Finish()
        {
            if (journal.Live == null) return;
            journal.Live.EndUtc = DateTime.UtcNow;
            if (journal.Live.EndUtc > journal.Live.StartUtc)
                journal.Completed.Add(journal.Live);
            journal.Live = null;
            TrySaveAndExport();
        }

        public void Checkpoint()
        {
            if (journal.Live != null)
                journal.Live.EndUtc = DateTime.UtcNow;
            TrySaveAndExport();
        }

        private void TrySaveAndExport()
        {
            try
            {
                AtomicWrite(journalPath, JsonSerializer.Serialize(journal,
                    new JsonSerializerOptions { WriteIndented = true }));
                ExportWeeks();
                LastError = "";
            }
            catch (Exception ex)
            {
                // The caller displays this in the app. Retry at the next checkpoint.
                LastError = ex.Message;
            }
        }

        private static void AtomicWrite(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(true));
            File.Move(temporary, path, true);
        }

        private static DateTime LocalMonday(DateTime utc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
            return local.Date.AddDays(-((int)local.DayOfWeek + 6) % 7);
        }

        private static string Duration(TimeSpan span)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}",
                (long)span.TotalHours, span.Minutes, span.Seconds);
        }

        private static string Hours(TimeSpan span)
        {
            return span.TotalHours.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string Csv(string s, bool spreadsheetText = false)
        {
            s = s ?? "";
            // Avoid Excel / PlanMaker interpreting user-provided project names as
            // executable spreadsheet formulas when opening an exported CSV.
            if (spreadsheetText && s.Length > 0 && "=+-@\t\r".Contains(s[0]))
                s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private void WriteExportIfChanged(string path, string content)
        {
            if (lastExportText.TryGetValue(path, out var previous) &&
                string.Equals(previous, content, StringComparison.Ordinal) && File.Exists(path))
                return;
            AtomicWrite(path, content);
            lastExportText[path] = content;
        }

        private void ExportWeeks()
        {
            var weeks = new Dictionary<DateTime, List<WeeklyPart>>();
            IEnumerable<Interval> source = journal.Completed;
            if (journal.Live != null)
                source = source.Concat(new[] { journal.Live });

            foreach (Interval entry in source)
            {
                if (entry == null || entry.EndUtc <= entry.StartUtc) continue;
                DateTime pos = DateTime.SpecifyKind(entry.StartUtc, DateTimeKind.Utc);
                DateTime end = DateTime.SpecifyKind(entry.EndUtc, DateTimeKind.Utc);
                while (pos < end)
                {
                    DateTime weekStart = LocalMonday(pos);
                    DateTime nextMondayLocal = DateTime.SpecifyKind(weekStart.AddDays(7), DateTimeKind.Unspecified);
                    DateTime nextMondayUtc = TimeZoneInfo.ConvertTimeToUtc(nextMondayLocal, TimeZoneInfo.Local);
                    DateTime stop = end < nextMondayUtc ? end : nextMondayUtc;
                    if (stop <= pos) throw new InvalidDataException("Invalid week boundary");
                    if (!weeks.TryGetValue(weekStart, out var parts))
                    {
                        parts = new List<WeeklyPart>();
                        weeks.Add(weekStart, parts);
                    }
                    parts.Add(new WeeklyPart { Project = entry.Project, StartUtc = pos, EndUtc = stop });
                    pos = stop;
                }
            }

            Directory.CreateDirectory(ExportFolder);
            foreach (var item in weeks)
            {
                DateTime monday = item.Key;
                DateTime sunday = monday.AddDays(6);
                string prefix = Path.Combine(ExportFolder,
                    "WorkLog_" + monday.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                    "_" + sunday.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

                var summary = new StringBuilder();
                summary.AppendLine("Week Start,Week End,Project,Duration (HH:MM:SS),Decimal Hours,Sessions");
                long totalTicks = 0;
                foreach (var group in item.Value.GroupBy(v => v.Project).OrderBy(v => v.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    long ticks = group.Sum(v => (v.EndUtc - v.StartUtc).Ticks);
                    totalTicks += ticks;
                    TimeSpan duration = TimeSpan.FromTicks(ticks);
                    summary.AppendLine(string.Join(",", Csv(monday.ToString("yyyy-MM-dd")),
                        Csv(sunday.ToString("yyyy-MM-dd")), Csv(group.Key, true),
                        Csv(Duration(duration)), Hours(duration),
                        group.Count().ToString(CultureInfo.InvariantCulture)));
                }
                var total = TimeSpan.FromTicks(totalTicks);
                summary.AppendLine(string.Join(",", Csv(monday.ToString("yyyy-MM-dd")),
                    Csv(sunday.ToString("yyyy-MM-dd")), Csv("TOTAL"),
                    Csv(Duration(total)), Hours(total),
                    item.Value.Count.ToString(CultureInfo.InvariantCulture)));
                WriteExportIfChanged(prefix + ".csv", summary.ToString());

                var details = new StringBuilder();
                details.AppendLine("Project,Start (local),End (local),Duration (HH:MM:SS),Decimal Hours");
                foreach (WeeklyPart part in item.Value.OrderBy(v => v.StartUtc))
                {
                    TimeSpan duration = part.EndUtc - part.StartUtc;
                    DateTimeOffset startLocal = TimeZoneInfo.ConvertTime(
                        new DateTimeOffset(part.StartUtc, TimeSpan.Zero), TimeZoneInfo.Local);
                    DateTimeOffset endLocal = TimeZoneInfo.ConvertTime(
                        new DateTimeOffset(part.EndUtc, TimeSpan.Zero), TimeZoneInfo.Local);
                    details.AppendLine(string.Join(",", Csv(part.Project, true),
                        Csv(startLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                        Csv(endLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                        Csv(Duration(duration)), Hours(duration)));
                }
                WriteExportIfChanged(prefix + "_sessions.csv", details.ToString());
            }
        }
    }
}
