using System.Net.Http.Json;

namespace TradingAutomationHub.Services;

public sealed class TelegramSettings
{
    public const string SectionName = "Telegram";
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}

public sealed class TelegramNotifier
{
    private readonly HttpClient _httpClient;
    private readonly TelegramSettings _settings;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(HttpClient httpClient, Microsoft.Extensions.Options.IOptions<TelegramSettings> settings, ILogger<TelegramNotifier> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.BotToken) && !string.IsNullOrWhiteSpace(_settings.ChatId);

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return;
        try
        {
            var payload = new { chat_id = _settings.ChatId, text = message, parse_mode = "HTML", disable_web_page_preview = true };
            using var response = await _httpClient.PostAsJsonAsync($"/bot{_settings.BotToken}/sendMessage", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Telegram notification failed: {Status} {Body}", response.StatusCode, body);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Telegram notification threw an exception.");
        }
    }
}
