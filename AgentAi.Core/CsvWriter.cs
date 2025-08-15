using System.Globalization;
using System.Text;

namespace AgentAi.Core;

public static class CsvWriter
{
    public static void WriteMatches(string path, List<MatchResult> rows)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        sw.WriteLine("order_line,item_name_raw,canonical_material,matching_confidence,supplier_id,company_name,contact_phone,contact_email,score,match_notes");
        foreach (var r in rows)
        {
            sw.WriteLine(string.Join(',', new[]
            {
                r.OrderLine.ToString(),
                Esc(r.ItemNameRaw),
                Esc(r.CanonicalMaterial),
                r.Confidence,
                Esc(r.SupplierId),
                Esc(r.CompanyName),
                Esc(r.ContactPhone),
                Esc(r.ContactEmail),
                r.Score.ToString(CultureInfo.InvariantCulture),
                Esc(r.MatchNotes)
            }));
        }
    }

    public static void WriteSummaries(string path, List<TopSuppliersSummary> rows)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        sw.WriteLine("canonical_material,total_qty,unit,rank,supplier_id,company_name,score");
        foreach (var r in rows)
        {
            int rank = 0;
            foreach (var t in r.Top)
            {
                rank++;
                sw.WriteLine(string.Join(',', new[]
                {
                    Esc(r.CanonicalMaterial),
                    r.TotalQty.ToString(CultureInfo.InvariantCulture),
                    Esc(r.Unit),
                    rank.ToString(),
                    Esc(t.SupplierId),
                    Esc(t.CompanyName),
                    t.Score.ToString("F3", CultureInfo.InvariantCulture)
                }));
            }
        }
    }

    private static string Esc(string s)
    {
        if (s == null) return string.Empty;
        if (s.Contains(',') || s.Contains('"')) return '"' + s.Replace("\"", "\"\"") + '"';
        return s;
    }
}
