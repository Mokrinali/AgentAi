using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentAi.Core;

public sealed class WhatsAppClient
{
    private readonly string _token;
    private readonly string _phoneNumberId;
    private readonly string _templateName;
    private readonly string _templateLang;

    private WhatsAppClient(string token, string phoneNumberId, string templateName, string templateLang)
    {
        _token = token;
        _phoneNumberId = phoneNumberId;
        _templateName = templateName;
        _templateLang = templateLang;
    }

    public static WhatsAppClient? FromEnv()
    {
        var token = Environment.GetEnvironmentVariable("WA_TOKEN");
        var pnid = Environment.GetEnvironmentVariable("WA_PHONE_NUMBER_ID");
        var tpl = Environment.GetEnvironmentVariable("WA_TEMPLATE_NAME") ?? "order_inquiry_ge";
        var lang = Environment.GetEnvironmentVariable("WA_TEMPLATE_LANG") ?? "ka";
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(pnid)) return null;
        return new WhatsAppClient(token, pnid, tpl, lang);
    }

    public async Task<bool> SendTemplateAsync(string toE164, string[] bodyParams)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var components = new List<object> {
            new {
                type = "body",
                parameters = bodyParams.Select(p => new { type = "text", text = p }).ToArray()
            }
        };

        var payload = new
        {
            messaging_product = "whatsapp",
            to = toE164,
            type = "template",
            template = new
            {
                name = _templateName,
                language = new { code = _templateLang },
                components = components
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var res = await http.PostAsync($"https://graph.facebook.com/v19.0/{_phoneNumberId}/messages",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            Console.WriteLine($"WA HTTP {(int)res.StatusCode}: {body}");
            return false;
        }
        return true;
    }
}
