using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using System.Text.RegularExpressions;

namespace Signum.Word;

internal static class SpreadsheetReindexer
{
    // An A1 reference inside a formula: optional $, 1-3 column letters, optional $, row digits.
    // Excludes refs glued to identifiers, function calls ("LOG10(") and cross-sheet refs ("Sheet1!A1").
    static readonly Regex CellRefRegex = new Regex(
        @"(?<![A-Za-z0-9_$!])(\$?)([A-Za-z]{1,3})(\$?)([0-9]+)(?![A-Za-z0-9_(])",
        RegexOptions.Compiled);

    // A sheet-qualified reference (optionally a range) as used in defined names: Sheet1!$A$1 or Sheet1!$A$1:$B$5.
    static readonly Regex SheetRefRegex = new Regex(
        @"(?:'(?<sheetq>[^']+)'|(?<sheet>[A-Za-z0-9_.]+))!(?<c1>\$?)(?<col1>[A-Za-z]{1,3})(?<r1>\$?)(?<row1>[0-9]+)(?::(?<c2>\$?)(?<col2>[A-Za-z]{1,3})(?<r2>\$?)(?<row2>[0-9]+))?",
        RegexOptions.Compiled);

    // A merge reference: top-left and bottom-right cell of the merged block, e.g. A12:C14.
    static readonly Regex MergeRangeRegex = new Regex(
        @"^([A-Za-z]{1,3})([0-9]+):([A-Za-z]{1,3})([0-9]+)$",
        RegexOptions.Compiled);

    public static void Reindex(SpreadsheetDocument document)
    {
        var workbookPart = document.WorkbookPart;
        if (workbookPart == null)
            return;

        var sheetRemaps = new Dictionary<string, Func<uint, bool, uint>>();
        var anyChanged = false;

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var remap = ReindexWorksheet(worksheetPart); // null when this sheet had no clone to renumber.
            if (remap != null)
            {
                anyChanged = true;
                var name = GetSheetName(workbookPart, worksheetPart);
                if (name != null)
                    sheetRemaps[name] = remap;
            }
        }

        if (!anyChanged)
            return;

        RemapDefinedNames(workbookPart, sheetRemaps);

        if (workbookPart.CalculationChainPart != null)
            workbookPart.DeletePart(workbookPart.CalculationChainPart); // Excel rebuilds it; a stale chain triggers a repair dialog.
    }

    // Returns a row remap (oldRow, isRangeEnd) => newRow for use by defined names, or null if nothing to do.
    static Func<uint, bool, uint>? ReindexWorksheet(WorksheetPart worksheetPart)
    {
        var worksheet = worksheetPart.Worksheet;
        var sheetData = worksheet?.GetFirstChild<S.SheetData>();
        if (worksheet == null || sheetData == null)
            return null;

        var rows = sheetData.Elements<S.Row>().ToList();
        if (rows.Count == 0)
            return null;

        // Renumber preserving gaps: a row whose old index jumped keeps that jump (empty rows stay empty),
        // while a @foreach clone (its index repeats the previous row's) only advances by one.
        var infos = new List<(S.Row Row, uint OldIndex, uint NewIndex)>(rows.Count);
        uint prevOld = 0, prevNew = 0;
        var shifted = false;
        foreach (var row in rows)
        {
            var oldIndex = row.RowIndex?.Value ?? prevOld + 1;
            var newIndex = prevNew + (oldIndex > prevOld ? oldIndex - prevOld : 1);
            shifted |= newIndex != oldIndex;
            infos.Add((row, oldIndex, newIndex));
            prevOld = oldIndex;
            prevNew = newIndex;
        }

        if (!shifted) // No clone shifted anything → leave the sheet (and its calcChain) untouched.
            return null;

        // Per old row, the ordered list of new rows it produced (NewIndex is ascending in infos order and
        // GroupBy preserves it, so the list is already sorted). A list of length > 1 means @foreach cloned the
        // row. A unique old index lies outside any loop; a clone's references span [first..last] of the produced
        // rows, so ranges enclosing the template body grow automatically.
        var oldToNews = infos.GroupBy(a => a.OldIndex)
            .ToDictionary(g => g.Key, g => g.Select(a => a.NewIndex).ToList());

        var sortedOld = oldToNews.Keys.OrderBy(a => a).ToList();

        uint RemapRow(uint oldRow, bool isRangeEnd)
        {
            if (oldToNews.TryGetValue(oldRow, out var news))
                return isRangeEnd ? news[^1] : news[0];

            return Interpolate(oldRow, sortedOld, oldToNews);
        }

        Func<uint, bool, uint> absoluteRemap = RemapRow; // refs outside any clone

        foreach (var (row, oldIndex, newIndex) in infos)
        {
            row.RowIndex = newIndex;

            // Body-local reference (e.g. =B3*C3 inside a clone) follows its own clone; anything else uses RemapRow.
            uint LocalRemap(uint old, bool end) => old == oldIndex ? newIndex : RemapRow(old, end);

            foreach (var cell in row.Elements<S.Cell>())
            {
                var cellRef = cell.CellReference?.Value;
                if (cellRef != null)
                    cell.CellReference = new string(cellRef.TakeWhile(char.IsLetter).ToArray()) + newIndex;

                var formula = cell.CellFormula;
                if (formula != null)
                {
                    if (formula.Text.HasText())
                        formula.Text = RemapRefsInText(formula.Text, LocalRemap);

                    var formulaRef = formula.Reference?.Value;
                    if (formulaRef != null)
                        formula.Reference = RemapRefsInText(formulaRef, LocalRemap);
                }
            }
        }

        // A merge lives in <mergeCells> (worksheet level), not in the cloned <row>s, so @foreach never
        // duplicated it. Re-emit one merge per clone of its rows so each repeated row keeps its merged cells.
        var mergeCells = worksheet.GetFirstChild<S.MergeCells>();
        if (mergeCells != null)
        {
            var newRefs = mergeCells.Elements<S.MergeCell>()
                .Where(mc => mc.Reference?.Value != null)
                .SelectMany(mc => ExpandMergeRef(mc.Reference!.Value!, oldToNews, absoluteRemap))
                .ToList();
            mergeCells.RemoveAllChildren();
            foreach (var r in newRefs)
                mergeCells.AppendChild(new S.MergeCell { Reference = r });
            mergeCells.Count = (uint)newRefs.Count; // Excel needs a matching count, else it shows a repair dialog.
        }

        worksheet.GetFirstChild<S.SheetDimension>()?.Remove(); // Let Excel recompute the used range.

        return absoluteRemap;
    }

    static string RemapRefsInText(string text, Func<uint, bool, uint> remap)
    {
        return CellRefRegex.Replace(text, m =>
        {
            var isRangeEnd = m.Index > 0 && text[m.Index - 1] == ':';
            var newRow = remap(uint.Parse(m.Groups[4].Value), isRangeEnd);
            return m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + newRow;
        });
    }

    // Split a merge reference into one reference per clone of its rows. When top and bottom row were cloned the
    // same number of times (i.e. the merge sits inside a @foreach body), emit one merge per clone, shifted to that
    // clone's rows. Otherwise fall back to the single Min/Max remap (merge outside any loop, or straddling one).
    static IEnumerable<string> ExpandMergeRef(string mergeRef, Dictionary<uint, List<uint>> oldToNews, Func<uint, bool, uint> absoluteRemap)
    {
        var m = MergeRangeRegex.Match(mergeRef);
        if (m.Success
            && oldToNews.TryGetValue(uint.Parse(m.Groups[2].Value), out var n1)
            && oldToNews.TryGetValue(uint.Parse(m.Groups[4].Value), out var n2)
            && n1.Count == n2.Count && n1.Count > 1)
        {
            // ponytail: clones paired by list index; a merge spanning two distinct loops cloned the same number
            // of times would pair wrong, but such a merge is nonsensical — the fallback below covers the rest.
            for (int i = 0; i < n1.Count; i++)
                yield return $"{m.Groups[1].Value}{n1[i]}:{m.Groups[3].Value}{n2[i]}";
        }
        else
            yield return RemapRefsInText(mergeRef, absoluteRemap);
    }

    // oldRow has no row in the output (e.g. a consumed marker row or an empty referenced row): shift it by the
    // expansion of the nearest known row above it, or leave it as-is if nothing above expanded. Best-effort, monotone.
    static uint Interpolate(uint oldRow, List<uint> sortedOld, Dictionary<uint, List<uint>> oldToNews)
    {
        uint? lower = sortedOld.Where(o => o < oldRow).Cast<uint?>().LastOrDefault();
        return lower == null ? oldRow : oldToNews[lower.Value][^1] + (oldRow - lower.Value);
    }

    static string? GetSheetName(WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        var id = workbookPart.GetIdOfPart(worksheetPart);
        return workbookPart.Workbook?.Sheets?.Elements<S.Sheet>()
            .FirstOrDefault(s => s.Id?.Value == id)?.Name?.Value;
    }

    static void RemapDefinedNames(WorkbookPart workbookPart, Dictionary<string, Func<uint, bool, uint>> sheetRemaps)
    {
        if (sheetRemaps.Count == 0)
            return;

        var definedNames = workbookPart.Workbook?.DefinedNames;
        if (definedNames == null)
            return;

        foreach (var dn in definedNames.Elements<S.DefinedName>())
        {
            if (!dn.Text.HasText())
                continue;

            dn.Text = SheetRefRegex.Replace(dn.Text, m =>
            {
                var sheet = m.Groups["sheetq"].Success ? m.Groups["sheetq"].Value : m.Groups["sheet"].Value;
                if (!sheetRemaps.TryGetValue(sheet, out var remap))
                    return m.Value;

                var prefix = m.Value.Substring(0, m.Value.IndexOf('!') + 1);
                var first = m.Groups["c1"].Value + m.Groups["col1"].Value + m.Groups["r1"].Value + remap(uint.Parse(m.Groups["row1"].Value), false);
                var second = m.Groups["row2"].Success
                    ? ":" + m.Groups["c2"].Value + m.Groups["col2"].Value + m.Groups["r2"].Value + remap(uint.Parse(m.Groups["row2"].Value), true)
                    : "";
                return prefix + first + second;
            });
        }
    }
}
