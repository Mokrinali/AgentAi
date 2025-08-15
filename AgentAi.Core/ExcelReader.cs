using System.Globalization;
using ClosedXML.Excel;

namespace AgentAi.Core;

public static class ExcelReader
{
    public static List<Supplier> ReadSuppliers(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Suppliers");
        var rows = ws.RangeUsed().RowsUsed().Skip(1);
        var headers = ws.Row(1).Cells().Select(c => c.GetString().Trim()).ToList();
        int Col(string name)
        {
            var idx = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new Exception($"[Suppliers] missing column '{name}'");
            return idx + 1;
        }
        var list = new List<Supplier>();
        foreach (var r in rows)
        {
            var s = new Supplier
            {
                SupplierId = r.Cell(Col("supplier_id")).GetString().Trim(),
                CompanyName = r.Cell(Col("company_name")).GetString().Trim(),
                IdNumber = r.Cell(Col("id_number")).GetString().Trim(),
                ContactPhone = r.Cell(Col("contact_phone")).GetString().Trim(),
                ContactEmail = r.Cell(Col("contact_email")).GetString().Trim(),
                MaterialsRaw = r.Cell(Col("materials")).GetString().Trim(),
                City = TryGet(r, Col, "city"),
                Notes = TryGet(r, Col, "notes")
            };
            if (!string.IsNullOrWhiteSpace(s.SupplierId)) list.Add(s);
        }
        return list;

        static string TryGet(IXLRow row, Func<string, int> Col, string name)
        {
            try { return row.Cell(Col(name)).GetString().Trim(); } catch { return string.Empty; }
        }
    }

    public static List<SynonymEntry> ReadSynonyms(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Synonyms");
        var rows = ws.RangeUsed().RowsUsed().Skip(1);
        var headers = ws.Row(1).Cells().Select(c => c.GetString().Trim()).ToList();
        int Col(string name)
        {
            var idx = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new Exception($"[Synonyms] missing column '{name}'");
            return idx + 1;
        }
        var list = new List<SynonymEntry>();
        foreach (var r in rows)
        {
            var e = new SynonymEntry
            {
                Canonical = r.Cell(Col("canonical_material")).GetString().Trim(),
                Synonym = r.Cell(Col("synonym")).GetString().Trim(),
                Category = TryGet(r, Col, "category"),
                Unit = TryGet(r, Col, "unit")
            };
            if (!string.IsNullOrWhiteSpace(e.Canonical) && !string.IsNullOrWhiteSpace(e.Synonym)) list.Add(e);
        }
        return list;

        static string TryGet(IXLRow row, Func<string, int> Col, string name)
        {
            try { return row.Cell(Col(name)).GetString().Trim(); } catch { return string.Empty; }
        }
    }

    public static List<OrderLine> ReadOrder(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Order");
        var rows = ws.RangeUsed().RowsUsed().Skip(1);
        var headers = ws.Row(1).Cells().Select(c => c.GetString().Trim()).ToList();
        int Col(string name)
        {
            var idx = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new Exception($"[Order] missing column '{name}'");
            return idx + 1;
        }
        var list = new List<OrderLine>();
        int ln = 0;
        foreach (var r in rows)
        {
            ln++;
            var itemName = r.Cell(Col("item_name")).GetString().Trim();
            if (string.IsNullOrWhiteSpace(itemName)) continue;
            double qty = 0;
            var qCell = r.Cell(Col("qty"));
            if (!qCell.IsEmpty()) double.TryParse(qCell.GetString().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out qty);
            var unit = TryGet(r, Col, "unit");
            list.Add(new OrderLine { LineNo = ln, ItemName = itemName, Qty = qty, Unit = unit });
        }
        return list;

        static string TryGet(IXLRow row, Func<string, int> Col, string name)
        {
            try { return row.Cell(Col(name)).GetString().Trim(); } catch { return string.Empty; }
        }
    }
}
