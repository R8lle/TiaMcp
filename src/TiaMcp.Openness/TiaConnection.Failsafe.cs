using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness;

// Fail-safe (F-) blocks: TIA Portal exports them as .s7dcl/.s7res, but refuses to import them
// that way ("The resource file contains corrupted data or format might be invalid"), even
// unchanged. The SimaticML XML route (PlcBlock.Export / PlcBlockComposition.Import) does work,
// provided the user is logged on to the safety program in TIA Portal. So an F-block is read with
// an extra '<name>.xml' and written from that file; its .s7dcl stays as the readable view.
public sealed partial class TiaConnection
{
    private static bool IsFailsafe(PlcBlock block) =>
        block.ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal);

    private static bool IsBlockXml(BlockDocument d) =>
        d.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && d.FileName.IndexOf(".stl-networks.", StringComparison.OrdinalIgnoreCase) < 0;

    private static string? ExportBlockXml(PlcBlock block, out string? error)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "fxml", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var file = new FileInfo(Path.Combine(dir.FullName, block.Name + ".xml"));
            block.Export(file, ExportOptions.WithDefaults);
            error = null;
            // DocumentInfo/Created is the export time - drop it, or every re-export would look like
            // a change made in TIA Portal.
            return Regex.Replace(File.ReadAllText(file.FullName), @"\r?\n?[ \t]*<Created>[^<]*</Created>", "");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    private ImportResult WriteFailsafeBlock(PlcSoftware software, PlcBlock block, PlcBlockComposition owner, IReadOnlyList<BlockDocument> documents)
    {
        // Checked first: an F-block import that TIA Portal refuses for a missing safety
        // login still leaves orphaned cross-references in the project ('object not available',
        // reproduced: 4 per refused attempt), which survive compiling and rebuilding the
        // cross-reference data. So don't even try without the login.
        var loginError = CheckSafetyLogin(software);
        if (loginError != null) return new ImportResult(false, new[] { loginError });

        // A failed F import is never retried in the same project state: its orphaned
        // cross-references only go away by closing the project without saving.
        if (_failsafeImportFailed != null)
        {
            return new ImportResult(false, new[]
            {
                $"REFUSED - nothing written: the F-block import of '{_failsafeImportFailed}' failed earlier in this project state. " +
                "Close the project in TIA Portal WITHOUT saving, open it again, call tia_connect, and only then write F-blocks again.",
            });
        }

        var xmlDoc = documents.FirstOrDefault(IsBlockXml);
        if (xmlDoc == null)
        {
            return new ImportResult(false, new[]
            {
                $"'{block.Name}' is a fail-safe block ({block.ProgrammingLanguage}). TIA Portal does not import F-blocks from .s7dcl - it is written from '{block.Name}.xml' (SimaticML). " +
                "Read the block again (read_source_tree with items) to get that file, make the change there, and write again.",
            });
        }

        // The block's identity comes from the XML's own <Name>: a different name would create a
        // second block instead of overwriting this one.
        string? xmlName;
        try
        {
            xmlName = XDocument.Parse(xmlDoc.Content).Descendants()
                .Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal))
                .Select(e => e.Elements().FirstOrDefault(a => a.Name.LocalName == "AttributeList")?.Elements().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            return new ImportResult(false, new[] { $"'{block.Name}.xml' is not valid XML: {ex.Message}" });
        }
        if (!string.Equals(xmlName, block.Name, StringComparison.Ordinal))
        {
            return new ImportResult(false, new[] { $"'{block.Name}.xml' names the block '{xmlName ?? "(none)"}' - it must stay '{block.Name}'. Renaming goes through rename_plc_block." });
        }

        // The .s7dcl of an F-block is only the readable view. An edit there that the .xml doesn't
        // carry would be lost without a word, so refuse instead.
        var dclDoc = documents.FirstOrDefault(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase));
        if (dclDoc != null)
        {
            // Same pipeline as read_source_tree (network titles patched in), so an unchanged
            // file compares equal.
            var current = ReadBlock(software, block).Documents;
            var currentXml = current.FirstOrDefault(IsBlockXml)?.Content;
            var currentDcl = current.FirstOrDefault(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase))?.Content;
            if (currentXml != null && currentDcl != null
                && Normalize(currentXml) == Normalize(xmlDoc.Content)
                && Normalize(currentDcl) != Normalize(dclDoc.Content))
            {
                return new ImportResult(false, new[]
                {
                    $"'{block.Name}' is a fail-safe block: its .s7dcl was changed, but TIA Portal can't import an F-block from .s7dcl. " +
                    $"Make the change in '{block.Name}.xml' instead (the .s7dcl is refreshed from the project after the write).",
                });
            }
        }

        // Orphaned cross-references ('object not available') from a failed import show up in the
        // cross references of the objects the block uses - counted before and after the import.
        var blockName = block.Name;
        var watched = GlobalNamesIn(xmlDoc.Content).Prepend(blockName).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        var orphansBefore = CountOrphanReferences(software, watched);

        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "fxml", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var file = new FileInfo(Path.Combine(dir.FullName, block.Name + ".xml"));
            File.WriteAllText(file.FullName, xmlDoc.Content);
            try
            {
                owner.Import(file, ImportOptions.Override);
            }
            catch (Exception importEx)
            {
                _failsafeImportFailed = blockName;
                var added = CountOrphanReferences(software, watched) - orphansBefore;
                return new ImportResult(false, new[]
                {
                    $"Import of fail-safe block '{blockName}' from XML failed: {importEx.Message}",
                    added > 0
                        ? $"STOP: the failed import left {added} orphaned cross-reference(s) ('object not available') in the project. Close the project WITHOUT saving and open it again - that is the only way to remove them. Nothing further is written until then."
                        : "No orphaned cross-references were found after the failed import. F-block writes stay blocked in this project state anyway - reconnect (tia_connect) after checking the project.",
                });
            }

            // An imported F-block stays inconsistent - and can't even be exported again - until
            // the safety program is compiled, so compile right away and report the outcome.
            var messages = new List<string> { "Fail-safe block written from SimaticML XML." };
            try
            {
                // Compiling just the block leaves it inconsistent; the safety program is generated
                // as a whole by the PLC software compile.
                var result = software.GetService<ICompilable>()?.Compile();
                if (result != null)
                {
                    messages.Add($"Compiled: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s).");
                    foreach (var m in Flatten(result.Messages).Where(m => m.State == CompilerResultState.Error).Take(20))
                        messages.Add($"[Error] {m.Path}: {m.Description}");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"(Written, but the compile failed to run: {ex.Message} - compile_plc it.)");
            }
            var orphansAfter = CountOrphanReferences(software, watched);
            if (orphansAfter > orphansBefore)
            {
                _failsafeImportFailed = blockName;
                messages.Add($"STOP: the import added {orphansAfter - orphansBefore} orphaned cross-reference(s) ('object not available'). Close the project WITHOUT saving and open it again. Nothing further is written until then.");
                return new ImportResult(false, messages);
            }
            messages.Add($"Cross-reference check: no new orphaned references ({orphansAfter} before and after, over {watched.Count} object(s)).");
            return new ImportResult(true, messages);
        }
        catch (Exception ex)
        {
            var hint = ex.Message.IndexOf("safety", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                ? " Log on to the safety program in TIA Portal first."
                : "";
            return new ImportResult(false, new[] { $"Import of fail-safe block '{block.Name}' from XML failed: {ex.Message}{hint}" });
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // Name of the F-block whose import failed in this connection; blocks further F imports.
    private string? _failsafeImportFailed;

    // First symbol component of every global access in a SimaticML block (DBs and PLC tags).
    private static IEnumerable<string> GlobalNamesIn(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants()
                .Where(e => e.Name.LocalName == "Access" && (string?)e.Attribute("Scope") == "GlobalVariable")
                .Select(e => e.Descendants().FirstOrDefault(c => c.Name.LocalName == "Component")?.Attribute("Name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int CountOrphanReferences(PlcSoftware software, IEnumerable<string> names)
    {
        var count = 0;
        foreach (var name in names)
        {
            try
            {
                IEngineeringServiceProvider? provider =
                    (IEngineeringServiceProvider?)FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, name)?.block
                    ?? FindTagByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, name);
                var service = provider?.GetService<CrossReferenceService>();
                if (service == null) continue;
                foreach (SourceObject source in service.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources)
                    count += CountOrphans(source);
            }
            catch
            {
                // object not resolvable right now - not counted
            }
        }
        return count;
    }

    private static int CountOrphans(SourceObject source)
    {
        static bool Placeholder(string? s) => s != null && s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal);
        var count = 0;
        foreach (ReferenceObject reference in source.References)
            foreach (Location location in reference.Locations)
                if (Placeholder(reference.Name) || Placeholder(location.ReferenceLocation)) count++;
        foreach (SourceObject child in source.Children) count += CountOrphans(child);
        return count;
    }

    private static string? CheckSafetyLogin(PlcSoftware software)
    {
        try
        {
            var admin = ((software.Parent as SoftwareContainer)?.Parent as DeviceItem)?.GetService<SafetyAdministration>();
            if (admin == null) return null; // no safety administration reachable - let TIA Portal decide
            if (!admin.IsSafetyOfflineProgramPasswordSet || admin.IsLoggedOnToSafetyOfflineProgram) return null;
            return "Not logged on to the safety program. Log on in TIA Portal (safety administration) and write again - " +
                   "an F-block import that TIA Portal refuses still leaves orphaned cross-references in the project.";
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<CompilerResultMessage> Flatten(CompilerResultMessageComposition messages)
    {
        foreach (var m in messages)
        {
            yield return m;
            foreach (var child in Flatten(m.Messages)) yield return child;
        }
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
