using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Keyloom.Engine;

/// <summary>Ollama runs models on this PC; OpenRouter reaches hosted ones, like Claude and Gemini, with a key.</summary>
public enum AiProvider { Ollama, OpenRouter }

public sealed record AiSetup(AiProvider Provider, string Model, string OllamaUrl = AiVision.DefaultOllamaUrl, string ApiKey = "")
{
    public string Where => Provider == AiProvider.Ollama ? L.T($"Ollama at {OllamaUrl}") : "OpenRouter";
}

/// <summary>Looks at a picture of the screen and answers a question about it. It only answers: it never clicks or types.</summary>
public interface IAiVision
{
    string Ask(string question, PixelImage? picture, CancellationToken token);
}

public sealed partial class AiVision(AiSetup setup, HttpClient? http = null) : IAiVision
{
    public const string DefaultOllamaUrl = "http://localhost:11434";
    private const string OpenRouterUrl = "https://openrouter.ai/api/v1";
    /// <summary>Pictures are made smaller than this on their longest side: enough to read a screen, and quicker and cheaper to send.</summary>
    public const int MaxSide = 1280;
    public const int MaxQuestion = 2000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    /// <summary>Tells the model what it is for: answering about the picture, briefly, and nothing else.</summary>
    public const string Instructions =
        "You are the eyes of a desktop macro tool. You get a screenshot and one question about it. " +
        "Answer briefly, with only the answer: no greeting, no explanation unless asked. " +
        "When asked for yes or no, answer only yes or no. When asked for a number, answer only the number. " +
        "You cannot click, type or control anything, so never give instructions.";
    private readonly HttpClient _http = http ?? Shared.Value;
    private static readonly Lazy<HttpClient> Shared = new(() => Client());
    public AiSetup Setup => setup;

    public static HttpClient Client(HttpMessageHandler? handler = null)
    {
        var client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Keyloom", "0.3"));
        return client;
    }

    public string Ask(string question, PixelImage? picture, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException(L.T("Ask the AI a question."));
        if (question.Length > MaxQuestion) throw new ArgumentException(L.T($"A question can be up to {MaxQuestion:N0} characters."));
        if (string.IsNullOrWhiteSpace(setup.Model)) throw new InvalidOperationException(L.T("Pick an AI model in Settings, under AI."));
        string? png = picture is { Width: > 0, Height: > 0 } p ? TargetPicture.Encode(Shrink(p, MaxSide)) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Patience);
        try { return AskAsync(question.Trim(), png, timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(L.T($"The AI took longer than {Patience.TotalSeconds:N0} seconds to answer."));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            throw new InvalidOperationException(setup.Provider == AiProvider.Ollama
                ? L.T($"Ollama is not answering at {setup.OllamaUrl}. Start Ollama, or check the address in Settings.")
                : L.T("OpenRouter could not be reached. Check the internet connection."));
        }
    }
    private async Task<string> AskAsync(string question, string? png, CancellationToken token)
    {
        using var request = setup.Provider == AiProvider.Ollama ? OllamaRequest(question, png) : OpenRouterRequest(question, png);
        using var response = await _http.SendAsync(request, token);
        string body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Refusal(response.StatusCode, body));
        JsonNode? json;
        try { json = JsonNode.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException(L.T("The AI sent back something Keyloom could not read.")); }
        string? answer = Text(setup.Provider == AiProvider.Ollama ? json?["message"]?["content"] : json?["choices"]?[0]?["message"]?["content"]);
        return Clean(answer ?? throw new InvalidOperationException(L.T("The AI sent back no answer.")));
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    private HttpRequestMessage OllamaRequest(string question, string? png)
    {
        var user = new JsonObject { ["role"] = "user", ["content"] = question };
        if (png != null) user["images"] = new JsonArray(png);
        var body = new JsonObject
        {
            ["model"] = setup.Model, ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Instructions }, user),
            ["options"] = new JsonObject { ["temperature"] = 0 }
        };
        return new HttpRequestMessage(HttpMethod.Post, setup.OllamaUrl.TrimEnd('/') + "/api/chat") { Content = Json(body) };
    }
    private HttpRequestMessage OpenRouterRequest(string question, string? png)
    {
        if (string.IsNullOrWhiteSpace(setup.ApiKey)) throw new InvalidOperationException(L.T("Add your OpenRouter key in Settings, under AI."));
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = question });
        if (png != null) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + png } });
        var body = new JsonObject
        {
            ["model"] = setup.Model, ["max_tokens"] = 300, ["temperature"] = 0,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Instructions }, new JsonObject { ["role"] = "user", ["content"] = content })
        };
        var request = new HttpRequestMessage(HttpMethod.Post, OpenRouterUrl + "/chat/completions") { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.ApiKey.Trim());
        request.Headers.Add("X-Title", "Keyloom");
        return request;
    }
    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
    private string Refusal(HttpStatusCode status, string body)
    {
        string said = "";
        try
        {
            var json = JsonNode.Parse(body);
            said = Text(json?["error"] is JsonObject e ? e["message"] : json?["error"]) ?? "";
        }
        catch (JsonException) { }
        return (setup.Provider, status) switch
        {
            (AiProvider.OpenRouter, HttpStatusCode.Unauthorized) => L.T("OpenRouter did not accept the key. Check it in Settings, under AI."),
            (AiProvider.OpenRouter, HttpStatusCode.PaymentRequired) => L.T("OpenRouter says the account is out of credit."),
            (AiProvider.OpenRouter, HttpStatusCode.TooManyRequests) => L.T("OpenRouter says too many questions were asked. Ask less often, or wait a moment."),
            (AiProvider.Ollama, HttpStatusCode.NotFound) => L.T($"Ollama does not have the model '{setup.Model}'. Get it with: ollama pull {setup.Model}"),
            _ when said.Contains("image", StringComparison.OrdinalIgnoreCase) || said.Contains("vision", StringComparison.OrdinalIgnoreCase)
                => L.T($"The model '{setup.Model}' cannot look at pictures. Pick one that can, in Settings."),
            _ => said.Length > 0 ? L.T($"The AI answered with an error ({(int)status}): {said}") : L.T($"The AI answered with an error ({(int)status}).")
        };
    }
    /// <summary>Some models wrap the answer in quotes or end it with a full stop; the answer is what is inside.</summary>
    private static string Clean(string answer)
    {
        answer = ThinkBlock().Replace(answer, "").Trim();
        if (answer.Length > 1 && answer[0] == '"' && answer[^1] == '"') answer = answer[1..^1].Trim();
        return answer;
    }
    [GeneratedRegex(@"<think>[\s\S]*?</think>")] private static partial Regex ThinkBlock();

    /// <summary>The models that can look at pictures, for the model list in Settings.</summary>
    public static async Task<IReadOnlyList<string>> ModelsAsync(AiSetup setup, HttpClient http, CancellationToken token = default)
    {
        bool ollama = setup.Provider == AiProvider.Ollama;
        try
        {
            using var response = await http.GetAsync(ollama ? setup.OllamaUrl.TrimEnd('/') + "/api/tags" : OpenRouterUrl + "/models", token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(L.T($"The model list could not be loaded ({(int)response.StatusCode})."));
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
            if (ollama)
                return [.. (json?["models"] as JsonArray ?? []).Select(m => Text(m?["name"])).OfType<string>().Order()];
            return [.. (json?["data"] as JsonArray ?? [])
                .Where(m => (m?["architecture"]?["input_modalities"] as JsonArray)?.Any(x => Text(x) == "image") == true)
                .Select(m => Text(m?["id"])).OfType<string>().Order()];
        }
        catch (HttpRequestException) when (ollama) { throw new InvalidOperationException(L.T($"Ollama is not answering at {setup.OllamaUrl}. Is it running?")); }
        catch (HttpRequestException) { throw new InvalidOperationException(L.T("OpenRouter could not be reached. Check the internet connection.")); }
        catch (JsonException) { throw new InvalidOperationException(L.T("The model list could not be read.")); }
    }

    /// <summary>Yes or no in an answer, in English or Polish; null when it is neither.</summary>
    public static bool? YesNo(string answer)
    {
        string first = Regex.Match(answer.ToLowerInvariant(), @"\p{L}+").Value;
        return first switch { "yes" or "y" or "true" or "tak" => true, "no" or "n" or "false" or "nie" => false, _ => null };
    }
    /// <summary>The first number in an answer, like 12 in "12 coins"; null when there is none.</summary>
    public static double? NumberIn(string answer)
    {
        var match = Regex.Match(answer.Replace(",", ""), @"-?\d+(\.\d+)?");
        return match.Success ? double.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }
    /// <summary>A smaller copy of a picture whose longest side is at most <paramref name="maxSide"/>, each new pixel the average of the ones it covers.</summary>
    public static PixelImage Shrink(PixelImage image, int maxSide)
    {
        int longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide) return image;
        double k = (double)longest / maxSide;
        int w = Math.Max(1, (int)(image.Width / k)), h = Math.Max(1, (int)(image.Height / k));
        var pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)(y * k), y1 = Math.Max(y0 + 1, Math.Min(image.Height, (int)((y + 1) * k)));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x * k), x1 = Math.Max(x0 + 1, Math.Min(image.Width, (int)((x + 1) * k)));
                long r = 0, g = 0, b = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        uint c = image.Pixels[yy * image.Width + xx];
                        r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; n++;
                    }
                pixels[y * w + x] = 0xFF000000 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
            }
        }
        return new(w, h, pixels);
    }
}
