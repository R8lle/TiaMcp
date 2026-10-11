using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
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
            var currentXml = ExportBlockXml(block, out _);
            var currentDcl = ExportDcl(block);
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

        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "fxml", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var file = new FileInfo(Path.Combine(dir.FullName, block.Name + ".xml"));
            File.WriteAllText(file.FullName, xmlDoc.Content);
            owner.Import(file, ImportOptions.Override);

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

    private static string? ExportDcl(PlcBlock block)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "fxml", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var result = block.ExportAsDocuments(dir, block.Name);
            if (result.State == DocumentResultState.Failure) return null;
            var dcl = result.ExportedDocuments.FirstOrDefault(f => f.Name.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase));
            return dcl == null ? null : File.ReadAllText(dcl.FullName);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
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
