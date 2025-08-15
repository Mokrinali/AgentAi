using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgentAi.Core;
using Microsoft.AspNetCore.Mvc;

namespace AgentAi.Web.Controllers;

[ApiController]
[Route("/")]
public class ProcessingController : ControllerBase
{
    private readonly ProcessingService _processor;
    private readonly ResultStore _store;

    public ProcessingController(ProcessingService processor, ResultStore store)
    {
        _processor = processor;
        _store = store;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile suppliers, IFormFile synonyms, IFormFile order)
    {
        if (suppliers == null || synonyms == null || order == null)
        {
            return BadRequest("Missing files");
        }

        await using var supStream = new MemoryStream();
        await suppliers.CopyToAsync(supStream);
        supStream.Position = 0;

        await using var synStream = new MemoryStream();
        await synonyms.CopyToAsync(synStream);
        synStream.Position = 0;

        await using var orderStream = new MemoryStream();
        await order.CopyToAsync(orderStream);
        orderStream.Position = 0;

        var result = await _processor.ProcessAsync(supStream, synStream, orderStream);
        var id = _store.Save(result);
        return Ok(new { id });
    }

    [HttpGet("results/{id:guid}")]
    public IActionResult GetResult(Guid id, [FromQuery] string type = "matches", [FromQuery] string format = "csv")
    {
        if (!_store.TryGet(id, out var result))
        {
            return NotFound();
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(result);
        }

        var content = string.Equals(type, "summary", StringComparison.OrdinalIgnoreCase)
            ? result.SummaryCsv
            : result.MatchesCsv;
        var fileName = string.Equals(type, "summary", StringComparison.OrdinalIgnoreCase) ? "summary.csv" : "matches.csv";
        return File(Encoding.UTF8.GetBytes(content), "text/csv", fileName);
    }
}
