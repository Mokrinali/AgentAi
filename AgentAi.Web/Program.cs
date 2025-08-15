using AgentAi.Core;
using AgentAi.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ProcessingService>();
builder.Services.AddSingleton<ResultStore>();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
