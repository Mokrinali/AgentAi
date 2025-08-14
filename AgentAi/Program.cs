// ProcurementBot — C# .NET 8 console app
// Features:
//  - Reads SUPPLIERS.xlsx (Sheet: Suppliers), MATERIAL_SYNONYMS.xlsx (Sheet: Synonyms), ORDER.xlsx (Sheet: Order)
//  - Normalizes + matches order items to canonical materials via synonyms + fuzzy matching
//  - Ranks suppliers per item; exports Matches CSV + Top Suppliers Summary CSV; previews WhatsApp messages
//  - Optional: send WhatsApp Cloud API template messages (requires approved template + Cloud API setup)
//
// Quick start:
//   dotnet new console -n ProcurementBot
//   cd ProcurementBot
//   dotnet add package ClosedXML
//
//   // Put this file as Program.cs (replace the auto-generated one)
//   // Build & Run (example):
//   dotnet run -- \
//     --sup "SUPPLIERS.xlsx" \
//     --syn "MATERIAL_SYNONYMS.xlsx" \
//     --order "ORDER.xlsx" \
//     --out "out" \
//     --mode balanced
//
//   // (Optional) WhatsApp Cloud API send (DRY-RUN by default; set --send-wa to actually call API):
//   // Required env vars:
//   //   WA_TOKEN=EAA... (Cloud API access token)
//   //   WA_PHONE_NUMBER_ID=123456789012345 (from Meta)
//   //   WA_TEMPLATE_NAME=order_inquiry_ge   (approved template name)
//   //   WA_TEMPLATE_LANG=ka
//   // Example run:
//   // dotnet run -- --sup SUPPLIERS.xlsx --syn MATERIAL_SYNONYMS.xlsx --order ORDER.xlsx --out out --mode balanced --send-wa
//
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

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

            var matcher = new Matcher(synonyms, mode: opts.Mode);
            Console.WriteLine("🔎 Building supplier material index...");
            var supplierIndex = matcher.BuildSupplierIndex(suppliers);

            Console.WriteLine("🧮 Matching...");
            var (matches, summaries) = matcher.MatchAll(order, suppliers, supplierIndex);

            // Export CSVs
            var matchesCsv = Path.Combine(opts.OutputDir, "matches.csv");
            var summaryCsv = Path.Combine(opts.OutputDir, "top_suppliers_summary.csv");
            CsvWriter.WriteMatches(matchesCsv, matches);
            CsvWriter.WriteSummaries(summaryCsv, summaries);

            Console.WriteLine($"📄 Exported:\n - {matchesCsv}\n - {summaryCsv}");

            // WhatsApp preview + optional send
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
        // Send one template message per canonical material to the #1 supplier (you can adjust to N top suppliers)
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

            // For demo: use 2 body parameters — material and quantity/unit combined (customize to your template)
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
internal enum MatchMode { Strict, Balanced, Fuzzy }

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
        // very small args parser: --key value
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

#region Models
internal sealed class Supplier
{
    public string SupplierId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string IdNumber { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string MaterialsRaw { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

internal sealed class SynonymEntry
{
    public string Canonical { get; set; } = string.Empty;
    public string Synonym { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
}

internal sealed class OrderLine
{
    public int LineNo { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public double Qty { get; set; }
    public string Unit { get; set; } = string.Empty;
}

internal sealed class MatchResult
{
    public int OrderLine { get; set; }
    public string ItemNameRaw { get; set; } = string.Empty;
    public string CanonicalMaterial { get; set; } = string.Empty;
    public string Confidence { get; set; } = "low"; // high/medium/low
    public string SupplierId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchNotes { get; set; } = string.Empty;
}

internal sealed class TopSuppliersSummary
{
    public string CanonicalMaterial { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double TotalQty { get; set; }
    public List<(string SupplierId, string CompanyName, double Score)> Top { get; set; } = new();
}
#endregion

#region Excel Reader
internal static class ExcelReader
{
    public static List<Supplier> ReadSuppliers(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Suppliers");
        var rows = ws.RangeUsed().RowsUsed().Skip(1); // skip header
        var list = new List<Supplier>();
        var headers = ws.Row(1).Cells().Select(c => c.GetString().Trim()).ToList();

        int Col(string name)
        {
            var idx = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new Exception($"[Suppliers] missing column '{name}'");
            return idx + 1; // ClosedXML is 1-based
        }

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
           
// In ExcelReader.ReadSuppliers, update the TryGet calls to pass r.WorksheetRow() instead of r

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
                    City = TryGet(r.WorksheetRow(), Col, "city"),
                    Notes = TryGet(r.WorksheetRow(), Col, "notes")
                };
                if (!string.IsNullOrWhiteSpace(s.SupplierId)) list.Add(s);
            }
// In ExcelReader.ReadSuppliers, update the TryGet calls to pass r.WorksheetRow() instead of r
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
                    City = TryGet(r.WorksheetRow(), Col, "city"),
                    Notes = TryGet(r.WorksheetRow(), Col, "notes")
                };
                if (!string.IsNullOrWhiteSpace(s.SupplierId)) list.Add(s);
            }
            
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
                    City = TryGet(r.WorksheetRow(), Col, "city"),
                    Notes = TryGet(r.WorksheetRow(), Col, "notes")
                };
                if (!string.IsNullOrWhiteSpace(s.SupplierId)) list.Add(s);
            }
                // In ExcelReader.ReadSuppliers, change TryGet(r, Col, "city") and TryGet(r, Col, "notes")
                // to pass r.WorksheetRow() instead of r

                City = TryGet(r.WorksheetRow(), Col, "city"),
                Notes = TryGet(r.WorksheetRow(), Col, "notes")
                City = TryGet(r, Col, "city"),
                Notes = TryGet(r, Col, "notes")
            };
            if (!string.IsNullOrWhiteSpace(s.SupplierId)) list.Add(s);
        }
        return list;

        // Update the TryGet method in ReadSuppliers to accept IXLRow instead of IXLRangeRow

        static string TryGet(IXLRow r, Func<string, int> Col, string name)
        {
            try { return r.Cell(Col(name)).GetString().Trim(); } catch { return string.Empty; }
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

        static string TryGet(IXLRow r, Func<string, int> Col, string name)
        {
            try { return r.Cell(Col(name)).GetString().Trim(); } catch { return string.Empty; }
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

        static string TryGet(IXLRow r, Func<string, int> Col, string name)
        {
            try { return r.Cell(Col(name)).GetString().Trim(); } catch { return string.Empty; }
        }
    }
}
#endregion

#region Matching Engine
internal sealed class Matcher
{
    private readonly Dictionary<string, string> _synToCanonical; // normalized synonym -> canonical
    private readonly HashSet<string> _canonicals; // normalized canonical set
    private readonly Dictionary<string, string> _canonicalUnits; // canonical -> unit (last seen)
    private readonly MatchMode _mode;

    public Matcher(List<SynonymEntry> synonyms, MatchMode mode)
    {
        _mode = mode;
        _synToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _canonicals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _canonicalUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in synonyms)
        {
            var canNorm = Normalizer.Norm(s.Canonical);
            var synNorm = Normalizer.Norm(s.Synonym);
            if (!_synToCanonical.ContainsKey(synNorm)) _synToCanonical[synNorm] = s.Canonical; // preserve original casing for output
            _canonicals.Add(canNorm);
            if (!string.IsNullOrWhiteSpace(s.Unit)) _canonicalUnits[s.Canonical] = s.Unit;
        }
    }

    public Dictionary<string, SupplierMaterialIndex> BuildSupplierIndex(List<Supplier> suppliers)
    {
        var index = new Dictionary<string, SupplierMaterialIndex>(); // supplierId -> index
        foreach (var s in suppliers)
        {
            var idx = new SupplierMaterialIndex(s.SupplierId);
            var parts = s.MaterialsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                var norm = Normalizer.Norm(p);
                // try direct synonym map (substring-aware)
                var (can, note, score) = MapToCanonical(norm);
                if (!string.IsNullOrWhiteSpace(can))
                {
                    idx.Add(can, norm, note, score);
                }
                else
                {
                    // If nothing found, store raw normalized for potential fuzzy later
                    idx.AddRaw(norm);
                }
            }
            index[s.SupplierId] = idx;
        }
        return index;
    }

    public (List<MatchResult> matches, List<TopSuppliersSummary> summaries) MatchAll(List<OrderLine> order, List<Supplier> suppliers, Dictionary<string, SupplierMaterialIndex> supplierIndex)
    {
        var matches = new List<MatchResult>();
        var byCanonical = new Dictionary<string, List<(string SupplierId, string CompanyName, double Score)>>();
        var qtyByCanonical = new Dictionary<string, double>();
        var unitByCanonical = new Dictionary<string, string>();

        foreach (var line in order)
        {
            var normOrder = Normalizer.Norm(line.ItemName);
            var (canonical, noteOrder, orderMapScore) = MapToCanonical(normOrder);
            string confidence;
            if (orderMapScore >= 0.85) confidence = "high";
            else if (orderMapScore >= 0.65) confidence = "medium";
            else confidence = "low";

            if (!string.IsNullOrWhiteSpace(canonical))
            {
                if (line.Qty > 0) qtyByCanonical[canonical] = qtyByCanonical.GetValueOrDefault(canonical) + line.Qty;
                if (!unitByCanonical.ContainsKey(canonical)) unitByCanonical[canonical] = !string.IsNullOrWhiteSpace(line.Unit) ? line.Unit : _canonicalUnits.GetValueOrDefault(canonical, "");

                foreach (var s in suppliers)
                {
                    var idx = supplierIndex[s.SupplierId];
                    var (score, supNote) = idx.ScoreForCanonical(canonical, this);
                    if (score < MinScoreForMode(_mode)) continue;

                    matches.Add(new MatchResult
                    {
                        OrderLine = line.LineNo,
                        ItemNameRaw = line.ItemName,
                        CanonicalMaterial = canonical,
                        Confidence = confidence,
                        SupplierId = s.SupplierId,
                        CompanyName = s.CompanyName,
                        ContactPhone = s.ContactPhone,
                        ContactEmail = s.ContactEmail,
                        Score = Math.Round(score, 3),
                        MatchNotes = $"order:{noteOrder}; supplier:{supNote}"
                    });

                    if (!byCanonical.ContainsKey(canonical)) byCanonical[canonical] = new();
                    byCanonical[canonical].Add((s.SupplierId, s.CompanyName, score));
                }
            }
            else
            {
                // no canonical found — still record a placeholder? Here we skip, but you can log.
                matches.Add(new MatchResult
                {
                    OrderLine = line.LineNo,
                    ItemNameRaw = line.ItemName,
                    CanonicalMaterial = "(ვერ მოიძებნა)",
                    Confidence = "low",
                    SupplierId = "-",
                    CompanyName = "-",
                    ContactPhone = "-",
                    ContactEmail = "-",
                    Score = 0,
                    MatchNotes = "სინონიმებში/ფაზიში ვერ მოიძებნა"
                });
            }
        }

        // Build summaries
        var summaries = new List<TopSuppliersSummary>();
        foreach (var kv in byCanonical)
        {
            var list = kv.Value
                .GroupBy(x => x.SupplierId)
                .Select(g => (SupplierId: g.Key, CompanyName: g.First().CompanyName, Score: g.Max(x => x.Score)))
                .OrderByDescending(x => x.Score)
                .Take(10)
                .ToList();
            summaries.Add(new TopSuppliersSummary
            {
                CanonicalMaterial = kv.Key,
                Unit = unitByCanonical.GetValueOrDefault(kv.Key, string.Empty),
                TotalQty = Math.Round(qtyByCanonical.GetValueOrDefault(kv.Key), 3),
                Top = list
            });
        }

        // order summaries by material name
        summaries = summaries.OrderBy(s => s.CanonicalMaterial, StringComparer.OrdinalIgnoreCase).ToList();

        // Sort matches: by OrderLine, Canonical, Score desc
        matches = matches
            .OrderBy(m => m.OrderLine)
            .ThenBy(m => m.CanonicalMaterial, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.Score)
            .ToList();

        return (matches, summaries);
    }

    public double MinScoreForMode(MatchMode mode) => mode switch
    {
        MatchMode.Strict => 0.85,
        MatchMode.Balanced => 0.60,
        MatchMode.Fuzzy => 0.40,
        _ => 0.60
    };

    public (string Canonical, string Note, double Score) MapToCanonical(string normalizedText)
    {
        // 1) Exact synonym match
        if (_synToCanonical.TryGetValue(normalizedText, out var can1))
            return (can1, "exact-synonym", 0.95);

        // 2) Any synonym contained as substring
        foreach (var kv in _synToCanonical)
        {
            if (normalizedText.Contains(kv.Key, StringComparison.Ordinal))
                return (kv.Value, $"contains:'{kv.Key}'", 0.90);
        }

        // 3) Fuzzy compare vs all synonyms
        string bestCan = string.Empty; double bestSim = 0; string bestSyn = string.Empty;
        foreach (var kv in _synToCanonical)
        {
            var sim = Similarity(normalizedText, kv.Key);
            if (sim > bestSim) { bestSim = sim; bestCan = kv.Value; bestSyn = kv.Key; }
        }
        if (bestSim >= 0.70) // threshold for canonical guess
        {
            double score = bestSim >= 0.85 ? 0.80 : bestSim >= 0.75 ? 0.65 : 0.55;
            return (bestCan, $"fuzzy:{bestSyn}~{bestSim:F2}", score);
        }

        return (string.Empty, "no-match", 0);
    }

    public (double Score, string Note) ScoreSupplierMaterial(string supplierMaterialNorm, string targetCanonical)
    {
        // Try map supplier material to canonical
        var (supCan, note, baseScore) = MapToCanonical(supplierMaterialNorm);
        if (string.IsNullOrWhiteSpace(supCan)) return (0, "no-canonical");

        // If canonical equals
        if (string.Equals(supCan, targetCanonical, StringComparison.OrdinalIgnoreCase))
        {
            // Distinguish strong vs medium (based on baseScore)
            var sc = baseScore >= 0.90 ? 1.0 : 0.90;
            return (sc, note);
        }

        // If different canonical, compute fuzzy between normalized canonicals (rare)
        var sim = Similarity(Normalizer.Norm(supCan), Normalizer.Norm(targetCanonical));
        if (sim >= 0.70) return (0.65, $"fuzzy-can:{sim:F2}");

        return (0, "canonical-mismatch");
    }

    private static double Similarity(string a, string b)
        => 1.0 - (double)LevenshteinDistance(a, b) / Math.Max(a.Length, b.Length);

    public static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int n = s.Length, m = t.Length;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }
}

internal sealed class SupplierMaterialIndex
{
    public string SupplierId { get; }
    private readonly List<string> _rawNormMaterials = new();
    private readonly List<(string Canonical, string SourceNorm, string Note, double Score)> _canonicals = new();

    public SupplierMaterialIndex(string supplierId) => SupplierId = supplierId;

    public void Add(string canonical, string sourceNorm, string note, double score)
        => _canonicals.Add((canonical, sourceNorm, note, score));

    public void AddRaw(string sourceNorm) => _rawNormMaterials.Add(sourceNorm);

    public (double Score, string Note) ScoreForCanonical(string targetCanonical, Matcher m)
    {
        double bestScore = 0; string bestNote = "";
        // Check mapped canonicals first (fast path)
        foreach (var e in _canonicals)
        {
            if (string.Equals(e.Canonical, targetCanonical, StringComparison.OrdinalIgnoreCase))
            {
                var sc = e.Score >= 0.90 ? 1.0 : 0.90; // exact -> 1.0; contains -> 0.90
                if (sc > bestScore) { bestScore = sc; bestNote = e.Note; }
            }
        }
        if (bestScore > 0) return (bestScore, bestNote);

        // Try fuzzy between other mapped canonicals
        foreach (var e in _canonicals)
        {
            var sim = 1.0 - (double)Matcher.LevenshteinDistance(Normalizer.Norm(e.Canonical), Normalizer.Norm(targetCanonical)) / Math.Max(e.Canonical.Length, targetCanonical.Length);
            if (sim >= 0.70)
            {
                var sc = 0.65;
                if (sc > bestScore) { bestScore = sc; bestNote = $"fuzzy-can:{sim:F2}"; }
            }
        }

        // As a last resort, try raw materials list vs target canonical
        foreach (var r in _rawNormMaterials)
        {
            var (sc, note) = m.ScoreSupplierMaterial(r, targetCanonical);
            if (sc > bestScore) { bestScore = sc; bestNote = note; }
        }

        return (bestScore, bestNote);
    }
}
#endregion

#region Normalizer + CSV Writer
internal static class Normalizer
{
    private static readonly Regex Punct = new("[\\p{P}\\p{S}]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled);

    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim().ToLowerInvariant();
        s = Punct.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        return s;
    }

    public static string NormalizePhoneE164(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("995")) return "+" + digits; // Georgia
        if (phone.StartsWith("+")) return phone; // assume already E.164
        // fallback: prefix '+'
        return "+" + digits;
    }
}

internal static class CsvWriter
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
        if (s == null) return "";
        if (s.Contains(',') || s.Contains('"')) return '"' + s.Replace("\"", "\"\"") + '"';
        return s;
    }
}
#endregion

#region WhatsApp Cloud API
internal sealed class WhatsAppClient
{
    private readonly string _token;
    private readonly string _phoneNumberId;
    private readonly string _templateName;
    private readonly string _templateLang;

    private WhatsAppClient(string token, string phoneNumberId, string templateName, string templateLang)
    { _token = token; _phoneNumberId = phoneNumberId; _templateName = templateName; _templateLang = templateLang; }

    public static WhatsAppClient? FromEnv()
    {
        var token = Environment.GetEnvironmentVariable("WA_TOKEN");
        var pnid = Environment.GetEnvironmentVariable("WA_PHONE_NUMBER_ID");
        var tpl = Environment.GetEnvironmentVariable("WA_TEMPLATE_NAME") ?? "order_inquiry_ge";
        var lang = Environment.GetEnvironmentVariable("WA_TEMPLATE_LANG") ?? "ka";
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(pnid)) return null;
        return new WhatsAppClient(token, pnid, tpl, lang);
    }

    public async Task<bool> SendTemplateAsync(string toE164, string[] bodyParams)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var components = new List<object> {
            new {
                type = "body",
                parameters = bodyParams.Select(p => new { type = "text", text = p }).ToArray()
            }
        };

        var payload = new
        {
            messaging_product = "whatsapp",
            to = toE164,
            type = "template",
            template = new
            {
                name = _templateName,
                language = new { code = _templateLang },
                components = components
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var res = await http.PostAsync($"https://graph.facebook.com/v19.0/{_phoneNumberId}/messages",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            Console.WriteLine($"WA HTTP {(int)res.StatusCode}: {body}");
            return false;
        }
        return true;
    }
}

internal static class WhatsAppMessageBuilder
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
#endregion
