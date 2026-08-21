using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace Oreo.TargetShieldHud
{
    /// <summary>
    /// Read-only link to the Oreo Roacher programmable block. The PB remains the
    /// owner of inventory scans and salvage records; this class only samples its
    /// existing LCD/Custom Data once per second.
    /// </summary>
    internal sealed class RoachT3Bridge
    {
        private const string LcdName = "RoachDataLCD";
        private const string DatabaseHeader = "========== ROACH DATABASE ==========";
        private const int ReadFrames = 60;
        private const int DiscoverFrames = 300;

        private readonly List<IMyProgrammableBlock> programmableBlocks =
            new List<IMyProgrammableBlock>();
        private IMyCubeGrid grid;
        private IMyTextPanel lcd;
        private IMyProgrammableBlock roacher;
        private int lastReadFrame = -ReadFrames;
        private int lastDiscoverFrame = -DiscoverFrames;
        private long lastSelectedTargetId;
        private T3Snapshot snapshot = new T3Snapshot();

        public bool IsLinked { get { return lcd != null && roacher != null; } }

        public T3Snapshot Read(IMyCubeGrid controlledGrid, long selectedTargetId,
            string selectedTargetName, int frame)
        {
            if (controlledGrid == null)
            {
                Reset();
                return snapshot;
            }

            if (grid == null || grid.EntityId != controlledGrid.EntityId)
            {
                Reset();
                grid = controlledGrid;
            }

            bool targetChanged = selectedTargetId != lastSelectedTargetId;
            if (lcd == null || roacher == null ||
                frame - lastDiscoverFrame >= DiscoverFrames)
                Discover(frame);

            if (targetChanged || frame - lastReadFrame >= ReadFrames)
            {
                lastSelectedTargetId = selectedTargetId;
                lastReadFrame = frame;
                snapshot = ReadSnapshot(selectedTargetId, selectedTargetName);
            }
            return snapshot;
        }

        public void Reset()
        {
            grid = null;
            lcd = null;
            roacher = null;
            lastSelectedTargetId = 0;
            lastReadFrame = -ReadFrames;
            lastDiscoverFrame = -DiscoverFrames;
            snapshot = new T3Snapshot();
            programmableBlocks.Clear();
        }

        private void Discover(int frame)
        {
            lastDiscoverFrame = frame;
            if (grid == null || MyAPIGateway.TerminalActionsHelper == null)
                return;

            IMyGridTerminalSystem terminal = MyAPIGateway.TerminalActionsHelper
                .GetTerminalSystemForGrid(grid);
            if (terminal == null)
                return;

            lcd = terminal.GetBlockWithName(LcdName) as IMyTextPanel;
            programmableBlocks.Clear();
            terminal.GetBlocksOfType(programmableBlocks, delegate(IMyProgrammableBlock block)
            {
                return block != null && block.CubeGrid != null &&
                    block.CubeGrid.EntityId == grid.EntityId;
            });

            roacher = null;
            for (int i = 0; i < programmableBlocks.Count; i++)
            {
                string data = programmableBlocks[i].CustomData ?? string.Empty;
                if (data.IndexOf(DatabaseHeader, StringComparison.Ordinal) >= 0 ||
                    data.IndexOf("T3STAT:", StringComparison.Ordinal) >= 0)
                {
                    roacher = programmableBlocks[i];
                    break;
                }
            }
            programmableBlocks.Clear();
        }

        private T3Snapshot ReadSnapshot(long selectedTargetId, string selectedTargetName)
        {
            var result = new T3Snapshot();
            if (lcd == null || roacher == null)
                return result;

            string trackLine = FindTrackLine(lcd.CustomData);
            if (string.IsNullOrEmpty(trackLine) ||
                trackLine.Equals("TRACK: OFF", StringComparison.OrdinalIgnoreCase))
                return result;

            string[] trackParts = trackLine.Split('|');
            if (trackParts.Length < 3)
                return result;

            result.Active = trackParts[0].IndexOf("ACTIVE",
                StringComparison.OrdinalIgnoreCase) >= 0;
            result.Name = trackParts[1].Trim();
            result.MatchesSelectedTarget = NamesMatch(result.Name, selectedTargetName);
            if (!result.MatchesSelectedTarget)
                return result;

            string valuePart = trackParts[2];
            int colon = valuePart.IndexOf(':');
            if (colon >= 0) valuePart = valuePart.Substring(colon + 1);
            string[] values = valuePart.Split('/');
            result.Current = ParseLeadingNumber(values[0]);
            if (values.Length > 1)
                result.ExpectedMaximum = ParseLeadingNumber(values[1]);

            string targetKey = NormalizeName(result.Name);
            string data = roacher.CustomData ?? string.Empty;
            string[] lines = data.Replace("\r", string.Empty).Split('\n');
            int bestKeyLength = -1;
            double savedRun = 0;
            bool savedRunFound = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("T3STAT:", StringComparison.Ordinal))
                {
                    string[] parts = line.Substring(7).Split('|');
                    if (parts.Length < 5)
                        continue;
                    string key = NormalizeName(parts[0]);
                    if (!KeyMatchesName(key, targetKey) || key.Length <= bestKeyLength)
                        continue;

                    result.Total = ParseNumber(parts[1]);
                    result.Best = ParseNumber(parts[2]);
                    result.Last = ParseNumber(parts[3]);
                    int tracked;
                    result.Tracked = int.TryParse(parts[4], out tracked) ? tracked : 0;
                    bestKeyLength = key.Length;
                }
                else if (line.StartsWith("T3RUN:", StringComparison.Ordinal))
                {
                    string[] parts = line.Substring(6).Split('|');
                    long id;
                    if (parts.Length == 3 && long.TryParse(parts[0], out id) &&
                        id == selectedTargetId)
                    {
                        savedRun = ParseNumber(parts[2]);
                        savedRunFound = true;
                    }
                }
            }

            // T3STAT/T3RUN are saved less frequently than the live TRACK header.
            // Add only the unsaved portion so the HUD stays current without asking
            // the PB to scan inventory or rewrite its large database more often.
            double unsaved = Math.Max(0, result.Current -
                (savedRunFound ? savedRun : 0));
            result.Total += unsaved;
            result.Best = Math.Max(result.Best, result.Current);
            if (result.Current > 0 && !savedRunFound)
                result.Tracked++;
            result.Average = result.Tracked > 0 ? result.Total / result.Tracked : 0;
            result.Available = true;
            return result;
        }

        private static string FindTrackLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripTags(lines[i]).Trim();
                if (line.StartsWith("TRACK:", StringComparison.OrdinalIgnoreCase))
                    return line;
            }
            return string.Empty;
        }

        private static string StripTags(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            int open;
            while ((open = value.IndexOf('<')) >= 0)
            {
                int close = value.IndexOf('>', open + 1);
                if (close < 0)
                    break;
                value = value.Remove(open, close - open + 1);
            }
            return value;
        }

        private static string NormalizeName(string value)
        {
            string name = StripTags(value ?? string.Empty).Trim().ToUpperInvariant();
            if (name.StartsWith("(NPC-", StringComparison.Ordinal))
            {
                int close = name.IndexOf(')');
                if (close >= 0 && close + 1 < name.Length)
                    name = name.Substring(close + 1).Trim();
            }
            return name;
        }

        private static bool NamesMatch(string first, string second)
        {
            string a = NormalizeName(first);
            string b = NormalizeName(second);
            return a.Length > 0 && b.Length > 0 &&
                (a == b || KeyMatchesName(a, b) || KeyMatchesName(b, a));
        }

        private static bool KeyMatchesName(string key, string name)
        {
            return key.Length > 0 && name.Length > 0 &&
                (key == name || name.IndexOf(key, StringComparison.Ordinal) >= 0);
        }

        private static double ParseLeadingNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            value = value.Trim();
            int length = 0;
            while (length < value.Length)
            {
                char c = value[length];
                if (!(char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == ','))
                    break;
                length++;
            }
            return ParseNumber(length > 0 ? value.Substring(0, length) : value);
        }

        private static double ParseNumber(string value)
        {
            double number;
            if (double.TryParse((value ?? string.Empty).Trim(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out number))
                return number;
            return double.TryParse(value, out number) ? number : 0;
        }
    }

    internal sealed class T3Snapshot
    {
        public bool Available;
        public bool Active;
        public bool MatchesSelectedTarget;
        public string Name = string.Empty;
        public double Current;
        public double ExpectedMaximum;
        public double Average;
        public double Best;
        public double Last;
        public double Total;
        public int Tracked;
    }
}
