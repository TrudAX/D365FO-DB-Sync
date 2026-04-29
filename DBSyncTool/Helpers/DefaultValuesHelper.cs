using System.Text.RegularExpressions;

namespace DBSyncTool.Helpers
{
    public static class DefaultValuesHelper
    {
        private const string FileName = "DefaultValues.ini";
        private static readonly Regex SectionHeader = new(@"^\[(.+)\]$", RegexOptions.Compiled);

        public static string GetDefaultFilePath()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(exeDir, FileName);
        }

        public static Dictionary<string, string> ReadAllSections(string filePath)
        {
            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(filePath))
                return sections;

            var lines = File.ReadAllLines(filePath);
            string? currentSection = null;
            var currentLines = new List<string>();

            foreach (var line in lines)
            {
                var match = SectionHeader.Match(line.Trim());
                if (match.Success)
                {
                    if (currentSection != null)
                        sections[currentSection] = JoinTrimmed(currentLines);

                    currentSection = match.Groups[1].Value;
                    currentLines.Clear();
                }
                else if (currentSection != null)
                {
                    currentLines.Add(line.TrimEnd());
                }
            }

            if (currentSection != null)
                sections[currentSection] = JoinTrimmed(currentLines);

            return sections;
        }

        public static string? ReadSection(string filePath, string sectionName)
        {
            var sections = ReadAllSections(filePath);
            return sections.TryGetValue(sectionName, out var content) ? content : null;
        }

        private static string JoinTrimmed(List<string> lines)
        {
            // Remove leading and trailing empty lines, preserve internal blank lines
            int start = 0;
            while (start < lines.Count && string.IsNullOrWhiteSpace(lines[start]))
                start++;

            int end = lines.Count - 1;
            while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
                end--;

            if (start > end)
                return "";

            return string.Join("\r\n", lines.GetRange(start, end - start + 1));
        }
    }
}
