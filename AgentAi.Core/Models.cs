namespace AgentAi.Core;

public sealed class Supplier
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

public sealed class SynonymEntry
{
    public string Canonical { get; set; } = string.Empty;
    public string Synonym { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
}

public sealed class OrderLine
{
    public int LineNo { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public double Qty { get; set; }
    public string Unit { get; set; } = string.Empty;
}

public sealed class MatchResult
{
    public int OrderLine { get; set; }
    public string ItemNameRaw { get; set; } = string.Empty;
    public string CanonicalMaterial { get; set; } = string.Empty;
    public string Confidence { get; set; } = "low";
    public string SupplierId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchNotes { get; set; } = string.Empty;
}

public sealed class TopSuppliersSummary
{
    public string CanonicalMaterial { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double TotalQty { get; set; }
    public List<(string SupplierId, string CompanyName, double Score)> Top { get; set; } = new();
}
