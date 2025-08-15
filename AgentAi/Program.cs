using System.Text;
using AgentAi.Core;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var opts = CliOptions.Parse(args);
            Directory.CreateDirectory(opts.OutputDir);

            Console.WriteLine("📦 Loading Excel files...");
            var suppliers = ExcelReader.ReadSuppliers(opts.SuppliersPath);
            var synonyms = ExcelReader.ReadSynonyms(opts.SynonymsPath);
            var order = ExcelReader.ReadOrder(opts.OrderPath);
            Console.WriteLine($"✅ Loaded: {suppliers.Count} suppliers, {synonyms.Count} synonym entries, {order.Count} order lines.");

            var matcher = new Matcher(synonyms, opts.Mode);
            Console.WriteLine("🔎 Building supplier material index...");
            var supplierIndex = matcher.BuildSupplierIndex(suppliers);

            Console.WriteLine("🧮 Matching...");
            var (matches, summaries) = matcher.MatchAll(order, suppliers, supplierIndex);

            var matchesCsv = Path.Combine(opts.OutputDir, "matches.csv");
            var summaryCsv = Path.Combine(opts.OutputDir, "top_suppliers_summary.csv");
            CsvWriter.WriteMatches(matchesCsv, matches);
            CsvWriter.WriteSummaries(summaryCsv, summaries);
            Console.WriteLine($"📄 Exported:\n - {matchesCsv}\n - {summaryCsv}");

            var waMsgsPath = Path.Combine(opts.OutputDir, "wa_messages_preview.txt");
            var waMsgs = WhatsAppMessageBuilder.BuildPreviews(order, summaries);
            await File.WriteAllTextAsync(waMsgsPath, waMsgs, Encoding.UTF8);
            Console.WriteLine($"💬 WhatsApp messages preview saved: {waMsgsPath}");

            if (opts.SendWhatsApp)
            {
                var wa = WhatsAppClient.FromEnv();
                if (wa == null)
                {
                    Console.WriteLine("⚠️ WA env vars missing. Set WA_TOKEN, WA_PHONE_NUMBER_ID, WA_TEMPLATE_NAME (optional), WA_TEMPLATE_LANG (optional). Skipping send.");
                }
                else
                {
                    Console.WriteLine("🚀 Sending WhatsApp template messages to top suppliers per material (be careful in production!)");
                    await SendWhatsAppToTopSuppliersAsync(wa, summaries, suppliers);
                }
            }

            Console.WriteLine("🎉 Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ Error: " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static async Task SendWhatsAppToTopSuppliersAsync(WhatsAppClient wa, List<TopSuppliersSummary> summaries, List<Supplier> suppliers)
    {
        foreach (var sum in summaries)
        {
            if (sum.Top == null || sum.Top.Count == 0) continue;
            var best = sum.Top[0];
            var sup = suppliers.FirstOrDefault(s => s.SupplierId == best.SupplierId);
            if (sup == null) continue;

            var to = Normalizer.NormalizePhoneE164(sup.ContactPhone);
            if (string.IsNullOrWhiteSpace(to))
            {
                Console.WriteLine($"⚠️ Skip WA send: invalid phone for {sup.CompanyName} ({sup.ContactPhone})");
                continue;
            }

            var materialParam = sum.CanonicalMaterial;
            var qtyParam = sum.TotalQty > 0 && !string.IsNullOrWhiteSpace(sum.Unit)
                ? $"{sum.TotalQty} {sum.Unit}"
                : "რაოდენობა/ერთეული მოგვწერეთ";

            var ok = await wa.SendTemplateAsync(to, new[] { materialParam, qtyParam });
            Console.WriteLine(ok
                ? $"✅ WA sent to {sup.CompanyName} ({to}) for {sum.CanonicalMaterial}"
                : $"❌ WA failed to {sup.CompanyName} ({to})");
        }
    }
}

#region CLI Options
internal sealed class CliOptions
{
    public string SuppliersPath { get; init; } = string.Empty;
    public string SynonymsPath { get; init; } = string.Empty;
    public string OrderPath { get; init; } = string.Empty;
    public string OutputDir { get; init; } = "out";
    public MatchMode Mode { get; init; } = MatchMode.Balanced;
    public bool SendWhatsApp { get; init; } = false;

    public static CliOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var key = args[i][2..];
                var val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                map[key] = val;
            }
        }

        string Get(string k, bool required = false)
        {
            if (map.TryGetValue(k, out var v)) return v;
            if (required) throw new Exception($"Missing --{k}");
            return string.Empty;
        }

        var modeStr = Get("mode");
        var mode = MatchMode.Balanced;
        if (!string.IsNullOrWhiteSpace(modeStr))
        {
            if (Enum.TryParse<MatchMode>(modeStr, true, out var m)) mode = m; else throw new Exception("Invalid --mode. Use strict|balanced|fuzzy");
        }

        return new CliOptions
        {
            SuppliersPath = Get("sup", required: true),
            SynonymsPath = Get("syn", required: true),
            OrderPath = Get("order", required: true),
            OutputDir = string.IsNullOrWhiteSpace(Get("out")) ? "out" : Get("out"),
            Mode = mode,
            SendWhatsApp = bool.TryParse(Get("send-wa"), out var b) && b
        };
    }
}
#endregion
