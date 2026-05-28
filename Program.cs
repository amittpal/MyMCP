using MyMCP.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// 1. Register the MCP server services with HTTP transport
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Optional configuration:
        // Enforce stateless HTTP routing rules
        options.Stateless = true;
        options.ConfigureSessionOptions = (httpContext, mcpServerOptions, cancellationToken) =>
        {
            var providedToken=httpContext.Request.Headers["Authorization"].ToString();
            var expectedToken=$"Bearer{Environment.GetEnvironmentVariable("MCP_AUTH_TOKEN")}";
            if (providedToken != expectedToken)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                throw new UnauthorizedAccessException("Invalid authentication token");
            }
                return Task.CompletedTask;

        };
    })
    .WithToolsFromAssembly(typeof(Program).Assembly); // Auto-discovers your custom [McpTool]s

builder.Services.AddSingleton<FruitsService>();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();
// 3. Map the base routing path for the protocol endpoints
// This automatically creates standard routes (e.g., /mcp/sse and /mcp/message)
app.MapMcp("/mcp");
app.MapControllers();
// 2. Required for routing standard endpoint requests
app.UseRouting();

app.Run();
