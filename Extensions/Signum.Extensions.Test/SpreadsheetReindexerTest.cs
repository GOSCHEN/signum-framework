using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Signum.Word;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Signum.Test.Word;

public class SpreadsheetReindexerTest
{
    [Fact]
    public void ClonedRows_RenumberAndSplitMerges()
    {
        using var ms = new MemoryStream();
        using var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook);

        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new S.Workbook();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();

        var sheetData = new S.SheetData(
            Row(1, Cell("A1"), Cell("B1"), Cell("C1")),
            Row(2, Cell("A2"), Cell("B2", styleIndex: 5), Cell("C2")), // body clone #1
            Row(2, Cell("A2"), Cell("B2", styleIndex: 5), Cell("C2"))  // body clone #2 (duplicate index)
        );
        var mergeCells = new S.MergeCells(new S.MergeCell { Reference = "B2:C2" }) { Count = 1 };
        wsPart.Worksheet = new S.Worksheet(sheetData, mergeCells);

        var sheets = wbPart.Workbook.AppendChild(new S.Sheets());
        sheets.AppendChild(new S.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Sheet1" });

        SpreadsheetReindexer.Reindex(doc);

        var rows = sheetData.Elements<S.Row>().ToList();
        Assert.Equal(new uint[] { 1, 2, 3 }, rows.Select(r => r.RowIndex!.Value));

        // All columns of both body clones survive with corrected references.
        Assert.Equal(new[] { "A2", "B2", "C2" }, rows[1].Elements<S.Cell>().Select(c => c.CellReference!.Value));
        Assert.Equal(new[] { "A3", "B3", "C3" }, rows[2].Elements<S.Cell>().Select(c => c.CellReference!.Value));

        // The reindexer never touches per-cell styling.
        Assert.Equal(5u, rows[2].Elements<S.Cell>().ElementAt(1).StyleIndex!.Value);

        // The fix: one merge per clone, not one collapsed block.
        var merges = mergeCells.Elements<S.MergeCell>().Select(m => m.Reference!.Value).ToList();
        Assert.Equal(new[] { "B2:C2", "B3:C3" }, merges);
        Assert.Equal(2u, mergeCells.Count!.Value);
    }

    static S.Row Row(uint index, params S.Cell[] cells) => new S.Row(cells) { RowIndex = index };

    static S.Cell Cell(string reference, uint? styleIndex = null) =>
        new S.Cell { CellReference = reference, StyleIndex = styleIndex };
}
