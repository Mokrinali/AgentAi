using System;
using System.Collections.Generic;
using AgentAi.Core;

namespace AgentAi.Web;

public class ResultStore
{
    private readonly Dictionary<Guid, ProcessingResult> _results = new();

    public Guid Save(ProcessingResult result)
    {
        var id = Guid.NewGuid();
        _results[id] = result;
        return id;
    }

    public bool TryGet(Guid id, out ProcessingResult result) => _results.TryGetValue(id, out result);
}
