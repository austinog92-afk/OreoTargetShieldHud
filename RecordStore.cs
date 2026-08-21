using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sandbox.ModAPI;

namespace Oreo.TargetShieldHud
{
    internal sealed class RecordStore
    {
        private const string FileName = "OreoTargetShieldHud.txt";
        public const string ExportFileName = "OreoTargetShieldHud-MaxShields.txt";
        private readonly Dictionary<string, double> highestMaxShield =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public bool Enabled = true;
        public double X = -0.34;
        public double Y = 0.82;
        public double Scale = 0.78;
        public bool EnemyBars = true;
        public bool MinimalEnemyBars = true;
        public bool Dirty { get; private set; }
        public int Count { get { return highestMaxShield.Count; } }

        public void Load()
        {
            highestMaxShield.Clear();
            Dirty = false;
            bool removedGenericRecord = false;
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInLocalStorage(FileName, typeof(Plugin)))
                    return;

                using (TextReader reader = MyAPIGateway.Utilities.ReadFileInLocalStorage(FileName, typeof(Plugin)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = line.Split('|');
                        if (parts.Length >= 5 && parts[0] == "SETTINGS")
                        {
                            bool enabled;
                            bool enemyBars;
                            bool minimalEnemyBars;
                            double x;
                            double y;
                            double scale;
                            if (bool.TryParse(parts[1], out enabled)) Enabled = enabled;
                            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) X = x;
                            if (double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) Y = y;
                            if (double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out scale)) Scale = scale;
                            if (parts.Length >= 6 && bool.TryParse(parts[5], out enemyBars)) EnemyBars = enemyBars;
                            if (parts.Length >= 7 && bool.TryParse(parts[6], out minimalEnemyBars)) MinimalEnemyBars = minimalEnemyBars;
                            continue;
                        }

                        if (parts.Length != 3 || parts[0] != "NPC")
                            continue;

                        double maximum;
                        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out maximum))
                            continue;

                        string name = Decode(parts[1]);
                        if (IsGenericGridName(name))
                        {
                            removedGenericRecord = true;
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(name) && maximum > 0)
                            highestMaxShield[name] = maximum;
                    }
                }
                Dirty = removedGenericRecord;
            }
            catch
            {
                highestMaxShield.Clear();
            }
        }

        public double Observe(string name, double maximumShield)
        {
            name = NormalizeName(name);
            if (string.IsNullOrEmpty(name) || IsGenericGridName(name) || maximumShield <= 0 ||
                double.IsNaN(maximumShield) || double.IsInfinity(maximumShield))
                return 0;

            double previous;
            if (!highestMaxShield.TryGetValue(name, out previous) || maximumShield > previous + 0.5)
            {
                highestMaxShield[name] = maximumShield;
                Dirty = true;
                return maximumShield;
            }
            return previous;
        }

        public int ClearShieldRecords()
        {
            int count = highestMaxShield.Count;
            highestMaxShield.Clear();
            Dirty = true;
            return count;
        }

        public int ExportShieldRecords()
        {
            if (MyAPIGateway.Utilities == null)
                return -1;

            using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(
                ExportFileName, typeof(Plugin)))
            {
                writer.WriteLine("=== OREO TARGET SHIELD MAXIMUMS ===");
                writer.WriteLine("Exported: " + DateTime.UtcNow.ToString(
                    "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
                writer.WriteLine("Records: " + highestMaxShield.Count);
                writer.WriteLine();
                writer.WriteLine("NPC NAME | MAX SHIELD HP");
                foreach (KeyValuePair<string, double> item in SortedRecords())
                {
                    writer.WriteLine(item.Key + " | " +
                        item.Value.ToString("0.##", CultureInfo.InvariantCulture));
                }
            }
            return highestMaxShield.Count;
        }

        public double GetMaximumSeen(string name)
        {
            if (IsGenericGridName(name))
                return 0;
            double value;
            return highestMaxShield.TryGetValue(NormalizeName(name), out value) ? value : 0;
        }

        public void MarkSettingsChanged()
        {
            Dirty = true;
        }

        public void ForceSave()
        {
            Dirty = true;
            Save();
        }

        public void Save()
        {
            if (!Dirty || MyAPIGateway.Utilities == null)
                return;

            using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(FileName, typeof(Plugin)))
            {
                writer.WriteLine("SETTINGS|" + Enabled + "|" +
                    X.ToString("R", CultureInfo.InvariantCulture) + "|" +
                    Y.ToString("R", CultureInfo.InvariantCulture) + "|" +
                    Scale.ToString("R", CultureInfo.InvariantCulture) + "|" +
                    EnemyBars + "|" + MinimalEnemyBars);
                foreach (KeyValuePair<string, double> item in SortedRecords())
                    writer.WriteLine("NPC|" + Encode(item.Key) + "|" +
                        item.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            Dirty = false;
        }

        public string Summary(int limit)
        {
            var text = new StringBuilder();
            int count = 0;
            foreach (KeyValuePair<string, double> item in SortedRecordsByValue())
            {
                if (count++ >= limit)
                    break;
                if (text.Length > 0)
                    text.Append("\n");
                text.Append(item.Key).Append(": ").Append(FormatNumber(item.Value)).Append(" HP");
            }
            return text.Length == 0 ? "No NPC shield records yet." : text.ToString();
        }

        private IEnumerable<KeyValuePair<string, double>> SortedRecords()
        {
            var records = new List<KeyValuePair<string, double>>(highestMaxShield);
            records.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
            return records;
        }

        private IEnumerable<KeyValuePair<string, double>> SortedRecordsByValue()
        {
            var records = new List<KeyValuePair<string, double>>(highestMaxShield);
            records.Sort((a, b) => b.Value.CompareTo(a.Value));
            return records;
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            return string.Join(" ", name.Trim().Split(new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        }

        public static bool IsGenericGridName(string name)
        {
            string value = NormalizeName(name);
            return IsGenericNameOrNumberedVariant(value, "Large Grid") ||
                IsGenericNameOrNumberedVariant(value, "Static Grid");
        }

        private static bool IsGenericNameOrNumberedVariant(string value, string genericName)
        {
            if (value.Equals(genericName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!value.StartsWith(genericName + " ", StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = value.Substring(genericName.Length + 1);
            if (suffix.Length == 0)
                return true;
            for (int i = 0; i < suffix.Length; i++)
            {
                if (!char.IsDigit(suffix[i]))
                    return false;
            }
            return true;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return string.Empty; }
        }

        private static string FormatNumber(double value)
        {
            if (value >= 1000000000) return (value / 1000000000d).ToString("0.##") + "B";
            if (value >= 1000000) return (value / 1000000d).ToString("0.##") + "M";
            if (value >= 1000) return (value / 1000d).ToString("0.##") + "K";
            return value.ToString("0");
        }
    }
}
