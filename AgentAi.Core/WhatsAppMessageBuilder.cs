using System.Text;

namespace AgentAi.Core;

public static class WhatsAppMessageBuilder
{
    private const string TemplateText =
        "გამარჯობა {company}, მაინტერესებს მასალა: {material} — რაოდენობა: {qtyunit}. გთხოვთ ფასი/ვადა. ({your_company}, {your_name}, {your_phone})";

    public static string BuildPreviews(List<OrderLine> order, List<TopSuppliersSummary> sums)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[WhatsApp Messages Preview]\n");
        foreach (var s in sums)
        {
            var qtyunit = s.TotalQty > 0 && !string.IsNullOrWhiteSpace(s.Unit) ? $"{s.TotalQty} {s.Unit}" : "N/A";
            int n = 0;
            foreach (var t in s.Top)
            {
                n++;
                sb.AppendLine($"Material: {s.CanonicalMaterial} | To: {t.CompanyName} ({t.SupplierId}) | Rank #{n} | Score {t.Score:F2}");
                var msg = TemplateText
                    .Replace("{company}", t.CompanyName)
                    .Replace("{material}", s.CanonicalMaterial)
                    .Replace("{qtyunit}", qtyunit)
                    .Replace("{your_company}", "YOUR_CO")
                    .Replace("{your_name}", "YOUR_NAME")
                    .Replace("{your_phone}", "+9955XXXXXXX");
                sb.AppendLine(msg);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
