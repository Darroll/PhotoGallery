using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace PhotoGallery;

/// <summary>A jersey number a model read on a photo, with how sure it was (0-100).</summary>
public record SeenNumber(string Number, int Confidence);

/// <summary>What one provider answered: the numbers, which provider and model, and how long it took.</summary>
public record VisionAnswer(List<SeenNumber> Numbers, string Provider, string Model, long Ms);

/// <summary>Sends a photo and a question to one AI provider. An interface so tests can stand in for real models.</summary>
public interface IVisionReader
{
    /// <summary>Asks one provider. Throws when the provider cannot be reached or answers with something unreadable.</summary>
    Task<VisionAnswer> AskAsync(VisionProvider provider, byte[] jpeg, string prompt, CancellationToken ct);
}

public class VisionReader : IVisionReader
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<VisionAnswer> AskAsync(VisionProvider p, byte[] jpeg, string prompt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var text = p.Kind switch
        {
            "ollama" => await OllamaAsync(p, jpeg, prompt, ct),
            "anthropic" => await AnthropicAsync(p, jpeg, prompt, ct),
            "openai" => await OpenAiAsync(p, jpeg, prompt, ct),
            _ => throw new InvalidOperationException("Unknown provider " + p.Kind),
        };
        return new VisionAnswer(PlayerPrompt.Parse(text), p.Kind, p.Model, sw.ElapsedMilliseconds);
    }

    static async Task<string> OllamaAsync(VisionProvider p, byte[] jpeg, string prompt, CancellationToken ct)
    {
        var body = new
        {
            model = p.Model,
            prompt,
            images = new[] { Convert.ToBase64String(jpeg) },
            stream = false,
            format = "json",
            options = new { temperature = 0 },
        };
        using var resp = await Http.PostAsJsonAsync($"{p.Url}/api/generate", body, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new VisionException($"Ollama answered {(int)resp.StatusCode}: {Short(raw)}");
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
    }

    static async Task<string> AnthropicAsync(VisionProvider p, byte[] jpeg, string prompt, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = p.ApiKey };
        var message = await client.Messages.Create(new MessageCreateParams
        {
            Model = p.Model,
            MaxTokens = 16000,
            OutputConfig = new OutputConfig { Effort = Effort.Low },   // reading a number is a simple task
            Messages =
            [
                new UserMessageParam
                {
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(jpeg), MediaType = MediaType.ImageJpeg } },
                        new TextBlockParam { Text = prompt },
                    },
                },
            ],
        }, ct);
        if (message.StopReason == StopReason.Refusal) throw new VisionException("Claude declined to look at this photo");
        return string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }

    static async Task<string> OpenAiAsync(VisionProvider p, byte[] jpeg, string prompt, CancellationToken ct)
    {
        var body = new
        {
            model = p.Model,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } },
                    },
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
        using var resp = await Http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new VisionException($"OpenAI answered {(int)resp.StatusCode}: {Short(raw)}");
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    static string Short(string s) => Regex.Replace(s, @"\s+", " ") is var t && t.Length > 200 ? t[..200] : Regex.Replace(s, @"\s+", " ");
}

/// <summary>A provider answered, but not in a usable way (an error status, a refusal, or no JSON).</summary>
public class VisionException(string message) : Exception(message);

/// <summary>The question asked about each photo, and reading the answer.</summary>
public static class PlayerPrompt
{
    /// <summary>
    /// Asks only about players in our team's colours, because the other team can wear the same numbers, and lists the roster
    /// so the model knows which numbers are possible. Asks for a confidence so unsure readings can wait for review.
    /// </summary>
    public static string Build(string teamColors, IEnumerable<string> rosterNumbers)
    {
        var colors = string.IsNullOrWhiteSpace(teamColors) ? "our team's jerseys" : teamColors.Trim();
        var numbers = string.Join(", ", rosterNumbers.Distinct().OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal));
        return "This is a photo from a youth sports game. Our team wears: " + colors + ". " +
               (numbers.Length > 0 ? "Our players' jersey numbers are: " + numbers + ". " : "") +
               "List the jersey numbers you can actually read on players wearing our team's jersey. " +
               "Ignore players in any other colors (the other team may wear the same numbers), referees, coaches, spectators, and numbers that are hidden, blurred or cut off. " +
               "Do not guess a number from a player's look or position. " +
               "Reply only with JSON: {\"numbers\": [{\"number\": \"10\", \"confidence\": 0-100}]}. Use an empty list when no number is readable.";
    }

    /// <summary>Reads the model's reply. Tolerates text around the JSON. Numbers are 1 to 3 digits; anything else is dropped.</summary>
    public static List<SeenNumber> Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) throw new VisionException("The answer had no JSON in it");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(reply[start..(end + 1)]); }
        catch (JsonException) { throw new VisionException("The answer was not valid JSON"); }
        using (doc)
        {
            var list = new List<SeenNumber>();
            if (!doc.RootElement.TryGetProperty("numbers", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray())
            {
                string? n = null; double conf = 50;
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("number", out var nv)) n = nv.ValueKind == JsonValueKind.Number ? nv.GetRawText() : nv.GetString();
                    if (item.TryGetProperty("confidence", out var cv))
                        conf = cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() : double.TryParse(cv.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 50;
                }
                else if (item.ValueKind is JsonValueKind.String or JsonValueKind.Number) n = item.ValueKind == JsonValueKind.Number ? item.GetRawText() : item.GetString();
                n = (n ?? "").Trim().TrimStart('#');
                if (!Regex.IsMatch(n, "^[0-9]{1,3}$")) continue;
                if (conf is > 0 and <= 1) conf *= 100;   // some models answer 0.9 for 90%
                var c = (int)Math.Round(Math.Clamp(conf, 0, 100));
                var existing = list.FindIndex(x => x.Number == n);
                if (existing >= 0) { if (list[existing].Confidence < c) list[existing] = new SeenNumber(n, c); }
                else list.Add(new SeenNumber(n, c));
            }
            return list;
        }
    }
}
