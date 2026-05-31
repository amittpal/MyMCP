using MyMCP.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
            var providedToken = httpContext.Request.Headers["Authorization"].ToString();
            var secret = Environment.GetEnvironmentVariable("MCP_AUTH_TOKEN");

            // Guardrail: Ensure the server is actually configured with a secret
            if (string.IsNullOrEmpty(secret))
            {
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                throw new InvalidOperationException("MCP_AUTH_TOKEN environment variable is not set.");
            }

            var expectedToken = $"Bearer {secret}";
            if (!string.Equals(providedToken, expectedToken, StringComparison.Ordinal))
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                throw new UnauthorizedAccessException("Invalid authentication token");
            }
                return Task.CompletedTask;

        };
    })
    .WithToolsFromAssembly(typeof(Program).Assembly); // Auto-discovers your custom [McpTool]s

// Operational Guardrail: Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    // 1. Throughput Limit: Max 10 requests per minute
    options.AddFixedWindowLimiter("mcp-policy", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromSeconds(60);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2;
    });

    // 2. Concurrency Limit: Max 3 requests running in parallel (at the same time)
    options.AddConcurrencyLimiter("mcp-concurrency", opt =>
    {
        opt.PermitLimit = 3; 
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2;
    });

    // Insight: Customizing the rejection response helps the AI/Client understand the failure
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) => {
        await context.HttpContext.Response.WriteAsync("The MCP server is currently throttled. Please try again later.", token);
    };
});

// Operational Guardrail: Request Timeouts
builder.Services.AddRequestTimeouts();

builder.Services.AddSingleton<StartupStatusService>();

// Register health check services
builder.Services.AddHealthChecks()
    // Liveness: Is the app process running?
    .AddCheck("Liveness", () => HealthCheckResult.Healthy(), tags: ["live"])
    // Readiness: Verify that required services are initialized and functional
    .AddCheck<FruitsReadinessHealthCheck>("Readiness", tags: ["ready"])
    // Startup: Has the app finished its initial boot sequence?
    .AddCheck<StartupHealthCheck>("Startup", tags: ["startup"]);

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

app.UseRouting();

// Apply the operational guardrail middleware
app.UseRateLimiter();
app.UseRequestTimeouts();

app.UseAuthorization();

// 3. Map the base routing path for the protocol endpoints
// This automatically creates standard routes (e.g., /mcp/sse and /mcp/message)
app.MapMcp("/mcp")
   .RequireRateLimiting("mcp-policy")      // Limit total volume
   .RequireRateLimiting("mcp-concurrency") // Limit actual parallelism
   .WithRequestTimeout(TimeSpan.FromSeconds(30)); // 30-second timeout for MCP operations

// 4. Map the health check endpoint
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
})
.AllowAnonymous()
.DisableRateLimiting();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
})
.AllowAnonymous()
.DisableRateLimiting();

app.MapHealthChecks("/health/startup", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("startup")
})
.AllowAnonymous()
.DisableRateLimiting();

app.MapControllers();

app.Run();

/// <summary>
/// Specialized health check for service readiness.
/// This implementation allows for clean Dependency Injection of required services.
/// </summary>
public class FruitsReadinessHealthCheck(FruitsService fruitsService) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool isReady = fruitsService.GetFruits().Length > 0;

        return Task.FromResult(isReady
            ? HealthCheckResult.Healthy("FruitsService is initialized and contains data.")
            : HealthCheckResult.Unhealthy("FruitsService is not yet ready or empty."));
    }
}

/// <summary>
/// Tracks whether the application has completed its startup tasks.
/// </summary>
public class StartupStatusService
{
    public bool StartupCompleted { get; set; } = false;
}

/// <summary>
/// Health check that returns healthy only after StartupStatusService.StartupCompleted is true.
/// </summary>
public class StartupHealthCheck(StartupStatusService startupStatusService) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (startupStatusService.StartupCompleted)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The startup sequence has completed."));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("The startup sequence is still in progress."));
    }
}
