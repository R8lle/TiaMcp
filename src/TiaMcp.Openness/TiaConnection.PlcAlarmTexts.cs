using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Alarm.TextLists;

namespace TiaMcp.Openness;

/// <summary>
/// PLC alarm text lists (PLC-Meldetextlisten). Openness only reaches the entries through the
/// Excel export/import of PlcAlarmTextListProvider, and its import replaces the complete content
/// of every list in the file. So a write always starts from a fresh export, changes only the
/// entries of the lists being written, and imports that file - lists and entries that are not
/// part of the write stay exactly as they were. Only user text lists are handled; system lists
/// (SYSTEM_*) belong to TIA Portal.
/// </summary>
public sealed partial class TiaConnection
{
    private static readonly XNamespace SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string EntrySheet = "TextListEntry";
    private const string ListSheet = "TextList";

    public IReadOnlyList<string> ListPlcAlarmTextListNames(string plcName)
    {
        EnsureConnected();
        return GetSoftware(plcName).PlcAlarmTextlistGroup.PlcAlarmUserTextlists.Select(l => l.Name).ToList();
    }

    public PlcAlarmTextListsResult ReadPlcAlarmTextLists(string plcName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var userLists = new HashSet<string>(software.PlcAlarmTextlistGroup.PlcAlarmUserTextlists.Select(l => l.Name), StringComparer.Ordinal);
        if (userLists.Count == 0) return new PlcAlarmTextListsResult(true, Array.Empty<PlcAlarmTextList>(), null);

        var tempDir = NewTempDir();
        try
        {
            var (book, error) = ExportTextBook(software, tempDir);
            if (book == null) return new PlcAlarmTextListsResult(error == null, Array.Empty<PlcAlarmTextList>(), error);
            return new PlcAlarmTextListsResult(true, ToLists(book, userLists), null);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    /// <summary>
    /// Writes the given user lists: each list's entries become exactly the given ones when
    /// deleteMissing is set, otherwise given entries are added/updated and the others kept.
    /// Language columns not given stay unchanged. With dryRun only the comparison is made.
    /// </summary>
    public PlcAlarmTextWriteResult WritePlcAlarmTextLists(string plcName, IReadOnlyList<PlcAlarmTextList> lists, bool deleteMissing, bool dryRun)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var provider = software.GetService<PlcAlarmTextListProvider>();
        if (provider == null) return new PlcAlarmTextWriteResult(false, Array.Empty<PlcAlarmTextListChange>(), $"PLC '{plcName}' offers no PlcAlarmTextListProvider service.");
        var userLists = new HashSet<string>(software.PlcAlarmTextlistGroup.PlcAlarmUserTextlists.Select(l => l.Name), StringComparer.Ordinal);

        var tempDir = NewTempDir();
        try
        {
            var (book, error) = ExportTextBook(software, tempDir);
            if (book == null)
                return new PlcAlarmTextWriteResult(false, Array.Empty<PlcAlarmTextListChange>(), error ?? "The PLC has no alarm text lists to write into - create the list in TIA Portal first.");

            // Input validation - TIA Portal terminates itself on an import file that doesn't match
            // its own format, so nothing is imported unless every list and language exists.
            var changes = new List<PlcAlarmTextListChange>();
            int cParent = book.Column("Parent"), cFrom = book.Column("From"), cTo = book.Column("To");
            var existingLists = new HashSet<string>(book.ListNames, StringComparer.Ordinal);
            foreach (var list in lists)
            {
                if (!userLists.Contains(list.Name) || !existingLists.Contains(list.Name))
                    return new PlcAlarmTextWriteResult(false, changes, $"Nothing was written: '{list.Name}' is not a user text list of PLC '{plcName}'. Create the list in TIA Portal first.");
                foreach (var language in list.Languages)
                    if (book.Column($"Text [{language}]") < 0)
                        return new PlcAlarmTextWriteResult(false, changes, $"Nothing was written: language '{language}' (list '{list.Name}') is not a project language. Languages: {string.Join(", ", book.Languages)}.");
            }

            foreach (var list in lists)
            {
                var change = new PlcAlarmTextListChange(list.Name);
                var rows = book.Rows.Where(r => r[cParent] == list.Name).ToList();
                var given = new HashSet<string>();
                foreach (var entry in list.Entries)
                {
                    var key = $"{entry.From}|{entry.To}";
                    given.Add(key);
                    var row = rows.FirstOrDefault(r => r[cFrom] == entry.From && r[cTo] == entry.To);
                    var isNew = row == null;
                    if (row == null)
                    {
                        row = Enumerable.Repeat("", book.Header.Count).ToList();
                        row[cParent] = list.Name;
                        row[cFrom] = entry.From;
                        row[cTo] = entry.To;
                        book.Rows.Add(row);
                    }
                    var diffs = new List<string>();
                    for (var i = 0; i < list.Languages.Count; i++)
                    {
                        var col = book.Column($"Text [{list.Languages[i]}]");
                        var value = i < entry.Texts.Count ? entry.Texts[i] : "";
                        if (NormalizeText(row[col]) == NormalizeText(value)) continue;
                        diffs.Add($"[{list.Languages[i]}] '{NormalizeText(row[col])}' -> '{value}'");
                        row[col] = value;
                    }
                    if (isNew) change.Added.Add($"{Range(entry.From, entry.To)}: {string.Join(" | ", list.Languages.Select((l, i) => $"[{l}] {(i < entry.Texts.Count ? entry.Texts[i] : "")}"))}");
                    else if (diffs.Count > 0) change.Changed.Add($"{Range(entry.From, entry.To)}: {string.Join("; ", diffs)}");
                }
                foreach (var row in rows.Where(r => !given.Contains($"{r[cFrom]}|{r[cTo]}")))
                {
                    if (!deleteMissing) continue;
                    change.Deleted.Add($"{Range(row[cFrom], row[cTo])}: {NormalizeText(row[book.Column($"Text [{book.Languages.FirstOrDefault()}]")])}");
                    book.Rows.Remove(row);
                }
                changes.Add(change);
            }

            if (dryRun || changes.All(c => c.Added.Count + c.Changed.Count + c.Deleted.Count == 0))
                return new PlcAlarmTextWriteResult(true, changes, null);

            var file = Path.Combine(tempDir, "write.xlsx");
            File.Copy(book.Path, file);
            book.Save(file);
            try
            {
                var import = provider.ImportFromXlsx(new FileInfo(file), ImportOptions.Override);
                var state = import.State.ToString();
                if (state is "OK" or "Success" or "Warning") return new PlcAlarmTextWriteResult(true, changes, state == "Warning" ? $"TIA Portal reported warnings, see {import.LogFilePath?.FullName}" : null);
                return new PlcAlarmTextWriteResult(false, changes, $"Import state {state}. Log: {import.LogFilePath?.FullName}");
            }
            catch (Exception ex)
            {
                return new PlcAlarmTextWriteResult(false, changes, DescribeException(ex));
            }
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    private static string Range(string from, string to) => from == to ? from : $"{from}..{to}";

    // TIA Portal exports a language without text as "<No value>".
    private static string NormalizeText(string? value) => value == "<No value>" ? "" : value ?? "";

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TiaMcp", "textlists", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A fresh export of all text lists; (null, null) when the PLC has none.
    private static (TextBook? Book, string? Error) ExportTextBook(Siemens.Engineering.SW.PlcSoftware software, string tempDir)
    {
        var provider = software.GetService<PlcAlarmTextListProvider>();
        if (provider == null) return (null, "The PLC offers no PlcAlarmTextListProvider service.");
        var file = Path.Combine(tempDir, "current.xlsx");
        try
        {
            var export = provider.ExportToXlsx(new FileInfo(file));
            var state = export.State.ToString();
            if (!(state is "OK" or "Success" or "Warning") || !File.Exists(file))
                return (null, $"Export of the alarm text lists failed (state {state}). Log: {export.LogFilePath?.FullName}");
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("TextListNotFound") || ex.InnerException?.GetType().Name.Contains("TextListNotFound") == true)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            return (null, DescribeException(ex));
        }
        return (TextBook.Load(file), null);
    }

    private static IReadOnlyList<PlcAlarmTextList> ToLists(TextBook book, ISet<string> userLists)
    {
        int cParent = book.Column("Parent"), cFrom = book.Column("From"), cTo = book.Column("To");
        var languages = book.Languages;
        return book.ListNames.Where(userLists.Contains).Select(name => new PlcAlarmTextList(name, languages,
            book.Rows.Where(r => r[cParent] == name)
                .Select(r => new PlcAlarmTextEntry(r[cFrom], r[cTo], languages.Select(l => NormalizeText(r[book.Column($"Text [{l}]")])).ToList()))
                .ToList())).ToList();
    }

    /// <summary>
    /// The TextListEntry sheet of a text list export, cells mapped by their column reference (the
    /// export writes cells sparsely). Saved back in TIA Portal's own form: shared strings.
    /// </summary>
    private sealed class TextBook
    {
        private TextBook(string path, string entrySheetPath, List<string> shared)
        {
            Path = path;
            EntrySheetPath = entrySheetPath;
            Shared = shared;
        }

        public string Path { get; }
        private string EntrySheetPath { get; }
        private List<string> Shared { get; }
        public List<string> Header { get; } = new();
        public List<List<string>> Rows { get; } = new();
        public List<string> ListNames { get; } = new();

        public int Column(string name) => Header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

        public List<string> Languages => Header.Where(h => h.StartsWith("Text [") && h.EndsWith("]")).Select(h => h.Substring(6, h.Length - 7)).ToList();

        public static TextBook Load(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            XDocument Read(string name)
            {
                var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} missing in {path}");
                using var stream = entry.Open();
                return XDocument.Load(stream);
            }

            var shared = zip.GetEntry("xl/sharedStrings.xml") == null ? new List<string>()
                : Read("xl/sharedStrings.xml").Root!.Elements(SheetNs + "si").Select(si => string.Concat(si.Descendants(SheetNs + "t").Select(t => t.Value))).ToList();
            var workbook = Read("xl/workbook.xml");
            var rels = Read("xl/_rels/workbook.xml.rels");
            string SheetPath(string sheetName)
            {
                var sheet = workbook.Descendants(SheetNs + "sheet").FirstOrDefault(s => (string?)s.Attribute("name") == sheetName)
                    ?? throw new InvalidDataException($"Sheet '{sheetName}' missing in the text list export");
                var id = (string?)sheet.Attribute(RelNs + "id");
                var target = rels.Root!.Elements().First(e => (string?)e.Attribute("Id") == id).Attribute("Target")!.Value.TrimStart('/');
                return target.StartsWith("xl/") ? target : "xl/" + target;
            }

            var book = new TextBook(path, SheetPath(EntrySheet), shared);
            var first = true;
            foreach (var row in ReadRows(Read(book.EntrySheetPath), shared))
            {
                if (first) { book.Header.AddRange(row); first = false; continue; }
                while (row.Count < book.Header.Count) row.Add("");
                if (row.Count > 0 && row[0].Length > 0) book.Rows.Add(row);
            }
            first = true;
            foreach (var row in ReadRows(Read(SheetPath(ListSheet)), shared))
            {
                if (first) { first = false; continue; }
                if (row.Count > 0 && row[0].Length > 0) book.ListNames.Add(row[0]);
            }
            return book;
        }

        private static IEnumerable<List<string>> ReadRows(XDocument sheet, List<string> shared)
        {
            foreach (var row in sheet.Descendants(SheetNs + "row"))
            {
                var cells = new SortedDictionary<int, string>();
                foreach (var c in row.Elements(SheetNs + "c"))
                {
                    var col = 0;
                    foreach (var ch in ((string)c.Attribute("r")!).TakeWhile(char.IsLetter)) col = col * 26 + (ch - 'A' + 1);
                    var type = (string?)c.Attribute("t");
                    cells[col - 1] = type == "s" ? shared[int.Parse(c.Element(SheetNs + "v")!.Value)]
                        : type == "inlineStr" ? string.Concat(c.Descendants(SheetNs + "t").Select(t => t.Value))
                        : c.Element(SheetNs + "v")?.Value ?? "";
                }
                yield return Enumerable.Range(0, cells.Count == 0 ? 0 : cells.Keys.Max() + 1).Select(i => cells.TryGetValue(i, out var v) ? v : "").ToList();
            }
        }

        // Replaces the entry sheet in a copy of the export; strings go through sharedStrings.xml
        // like in TIA Portal's own file (the other sheets keep their shared string indexes).
        public void Save(string target)
        {
            var strings = new List<string>(Shared);
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < strings.Count; i++) if (!index.ContainsKey(strings[i])) index[strings[i]] = i;
            int Id(string value)
            {
                if (!index.TryGetValue(value, out var id)) { id = strings.Count; strings.Add(value); index[value] = id; }
                return id;
            }
            static string ColumnName(int i)
            {
                var name = "";
                for (var n = i + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
                return name;
            }
            XElement Row(int number, IReadOnlyList<string> values) => new(SheetNs + "row", new XAttribute("r", number),
                values.Select((v, i) => new XElement(SheetNs + "c", new XAttribute("r", ColumnName(i) + number), new XAttribute("s", 0), new XAttribute("t", "s"),
                    new XElement(SheetNs + "v", Id(v ?? "")))));

            var sheet = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(SheetNs + "worksheet", new XElement(SheetNs + "sheetData",
                    new[] { Row(1, Header) }.Concat(Rows.Select((r, i) => Row(i + 2, r))))));
            var sst = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(SheetNs + "sst", new XAttribute("uniqueCount", strings.Count),
                    strings.Select(s => new XElement(SheetNs + "si", new XElement(SheetNs + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), s)))));

            using var zip = ZipFile.Open(target, ZipArchiveMode.Update);
            void Replace(string name, XDocument doc)
            {
                zip.GetEntry(name)?.Delete();
                using var stream = zip.CreateEntry(name).Open();
                doc.Save(stream);
            }
            Replace(EntrySheetPath, sheet);
            Replace("xl/sharedStrings.xml", sst);
        }
    }
}
