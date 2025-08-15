using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentAi.Core;

public class ProcessingService
{
    public Task<ProcessingResult> ProcessAsync(Stream suppliers, Stream synonyms, Stream order, CancellationToken ct = default)
    {
        // Placeholder processing implementation. Integrate matching logic here.
        var matches = new StringBuilder();
        matches.AppendLine("canonical_material,total_qty,unit,rank,supplier_id,company_name,score");

        var summary = new StringBuilder();
        summary.AppendLine("canonical_material,total_qty,unit,rank,supplier_id,company_name,score");

        var result = new ProcessingResult(matches.ToString(), summary.ToString());
        return Task.FromResult(result);
    }
}
