namespace ExchangeAdminWeb.Services;

public sealed class TestAccountPoolCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModuleEnablementService _enablement;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ILogger<TestAccountPoolCleanupWorker> _logger;

    public TestAccountPoolCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ModuleEnablementService enablement,
        ModuleConfigService moduleConfig,
        ILogger<TestAccountPoolCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _enablement = enablement;
        _moduleConfig = moduleConfig;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetInterval();
            try
            {
                await Task.Delay(delay, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;

                if (!_enablement.IsModuleEnabled("TestAccountPool"))
                    continue;

                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TestAccountPoolService>();
                if (!service.IsOnPremPoolConfigured)
                    continue;

                var result = await service.CleanupExpiredAsync("System", "BackgroundWorker");
                if (!result.Success && !result.Message.Contains("No expired accounts", StringComparison.OrdinalIgnoreCase))
                    _logger.LogWarning("Test account pool cleanup failed: {Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in test account pool cleanup worker");
            }
        }
    }

    private TimeSpan GetInterval()
    {
        var configured = _moduleConfig.GetValue("TestAccountPool", "CleanupIntervalMinutes");
        return int.TryParse(configured, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 1440))
            : TimeSpan.FromMinutes(30);
    }
}
