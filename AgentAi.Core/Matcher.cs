namespace AgentAi.Core;

public sealed class Matcher
{
    private readonly Dictionary<string, string> _synToCanonical;
    private readonly HashSet<string> _canonicals;
    private readonly Dictionary<string, string> _canonicalUnits;
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
            if (!_synToCanonical.ContainsKey(synNorm))
                _synToCanonical[synNorm] = s.Canonical;
            _canonicals.Add(canNorm);
            if (!string.IsNullOrWhiteSpace(s.Unit))
                _canonicalUnits[s.Canonical] = s.Unit;
        }
    }

    public Dictionary<string, SupplierMaterialIndex> BuildSupplierIndex(List<Supplier> suppliers)
    {
        var index = new Dictionary<string, SupplierMaterialIndex>();
        foreach (var s in suppliers)
        {
            var idx = new SupplierMaterialIndex(s.SupplierId);
            var parts = s.MaterialsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                var norm = Normalizer.Norm(p);
                var (can, note, score) = MapToCanonical(norm);
                if (!string.IsNullOrWhiteSpace(can))
                    idx.Add(can, norm, note, score);
                else
                    idx.AddRaw(norm);
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
            string confidence = orderMapScore switch
            {
                >= 0.85 => "high",
                >= 0.65 => "medium",
                _ => "low"
            };

            if (!string.IsNullOrWhiteSpace(canonical))
            {
                if (line.Qty > 0) qtyByCanonical[canonical] = qtyByCanonical.GetValueOrDefault(canonical) + line.Qty;
                if (!unitByCanonical.ContainsKey(canonical))
                    unitByCanonical[canonical] = !string.IsNullOrWhiteSpace(line.Unit) ? line.Unit : _canonicalUnits.GetValueOrDefault(canonical, "");

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
        summaries = summaries.OrderBy(s => s.CanonicalMaterial, StringComparer.OrdinalIgnoreCase).ToList();
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
        if (_synToCanonical.TryGetValue(normalizedText, out var can1))
            return (can1, "exact-synonym", 0.95);

        foreach (var kv in _synToCanonical)
        {
            if (normalizedText.Contains(kv.Key, StringComparison.Ordinal))
                return (kv.Value, $"contains:'{kv.Key}'", 0.90);
        }

        string bestCan = string.Empty; double bestSim = 0; string bestSyn = string.Empty;
        foreach (var kv in _synToCanonical)
        {
            var sim = Similarity(normalizedText, kv.Key);
            if (sim > bestSim) { bestSim = sim; bestCan = kv.Value; bestSyn = kv.Key; }
        }
        if (bestSim >= 0.70)
        {
            double score = bestSim >= 0.85 ? 0.80 : bestSim >= 0.75 ? 0.65 : 0.55;
            return (bestCan, $"fuzzy:{bestSyn}~{bestSim:F2}", score);
        }

        return (string.Empty, "no-match", 0);
    }

    public (double Score, string Note) ScoreSupplierMaterial(string supplierMaterialNorm, string targetCanonical)
    {
        var (supCan, note, baseScore) = MapToCanonical(supplierMaterialNorm);
        if (string.IsNullOrWhiteSpace(supCan)) return (0, "no-canonical");

        if (string.Equals(supCan, targetCanonical, StringComparison.OrdinalIgnoreCase))
        {
            var sc = baseScore >= 0.90 ? 1.0 : 0.90;
            return (sc, note);
        }

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

public sealed class SupplierMaterialIndex
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
        double bestScore = 0; string bestNote = string.Empty;

        foreach (var e in _canonicals)
        {
            if (string.Equals(e.Canonical, targetCanonical, StringComparison.OrdinalIgnoreCase))
            {
                var sc = e.Score >= 0.90 ? 1.0 : 0.90;
                if (sc > bestScore) { bestScore = sc; bestNote = e.Note; }
            }
        }
        if (bestScore > 0) return (bestScore, bestNote);

        foreach (var e in _canonicals)
        {
            var sim = 1.0 - (double)Matcher.LevenshteinDistance(Normalizer.Norm(e.Canonical), Normalizer.Norm(targetCanonical)) / Math.Max(e.Canonical.Length, targetCanonical.Length);
            if (sim >= 0.70)
            {
                var sc = 0.65;
                if (sc > bestScore) { bestScore = sc; bestNote = $"fuzzy-can:{sim:F2}"; }
            }
        }

        foreach (var r in _rawNormMaterials)
        {
            var (sc, note) = m.ScoreSupplierMaterial(r, targetCanonical);
            if (sc > bestScore) { bestScore = sc; bestNote = note; }
        }

        return (bestScore, bestNote);
    }
}

