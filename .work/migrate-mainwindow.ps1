$p = 'E:\Projects_MT5\XYN\MainWindow.xaml.cs'
$s = [IO.File]::ReadAllText($p)
$s = $s.Replace('using System.Net.Http;', 'using Newtonsoft.Json.Linq;`nusing RestSharp;')
$s = $s.Replace('using System.Text.Json;', '')
$s = $s.Replace('private static readonly HttpClient signalHttpClient = new() { BaseAddress = new Uri("https://tradersview.deriv.com") };', 'private static readonly RestClient signalClient = new(new RestClientOptions("https://tradersview.deriv.com") { Timeout = TimeSpan.FromSeconds(20) });')
$s = $s.Replace('private static readonly HttpClient bridgeHealthClient = new() { BaseAddress = new Uri("http://127.0.0.1:8000"), Timeout = TimeSpan.FromSeconds(2) };', 'private static readonly RestClient bridgeHealthClient = new(new RestClientOptions("http://127.0.0.1:8000") { Timeout = TimeSpan.FromSeconds(2) });')
$s = $s.Replace('using var response = await bridgeHealthClient.GetAsync("/health");', 'var response = await bridgeHealthClient.ExecuteAsync(new RestRequest("/health", Method.Get));')
$s = $s.Replace('response.IsSuccessStatusCode', 'response.IsSuccessful')
$s = $s.Replace('using var response = await signalHttpClient.GetAsync(requestUrl);', 'var response = await signalClient.ExecuteAsync(new RestRequest(requestUrl, Method.Get));')
$s = $s.Replace('var content = await response.Content.ReadAsStringAsync();', 'var content = response.Content ?? string.Empty;')
$s = $s.Replace('var root = document.RootElement;', 'var root = JObject.Parse(content);')
$s = $s.Replace('using var document = JsonDocument.Parse(content);', '')
$s = $s.Replace("using System.Net.Http;`r`nusing System.Text.Json;`r`n", "using Newtonsoft.Json.Linq;`r`nusing RestSharp;`r`n")
$s = $s.Replace('    private static readonly HttpClient signalHttpClient = new() { BaseAddress = new Uri("https://tradersview.deriv.com") };`r`n    private static readonly HttpClient bridgeHealthClient = new() { BaseAddress = new Uri("http://127.0.0.1:8000"), Timeout = TimeSpan.FromSeconds(2) };', '    private static readonly RestClient signalClient = new(new RestClientOptions("https://tradersview.deriv.com") { Timeout = TimeSpan.FromSeconds(20) });`r`n    private static readonly RestClient bridgeHealthClient = new(new RestClientOptions("http://127.0.0.1:8000") { Timeout = TimeSpan.FromSeconds(2) });')
$s = $s.Replace('                using var response = await bridgeHealthClient.GetAsync("/health");`r`n                if (response.IsSuccessStatusCode) return true;', '                var response = await bridgeHealthClient.ExecuteAsync(new RestRequest("/health", Method.Get));`r`n                if (response.IsSuccessful) return true;')
$s = $s.Replace('            catch (HttpRequestException) { }`r`n', '')
$s = $s.Replace('            using var response = await signalHttpClient.GetAsync(requestUrl);`r`n            var content = await response.Content.ReadAsStringAsync();`r`n            SignalRequestText.Text = $"{(int)response.StatusCode} {response.ReasonPhrase} · timeframe={timeframe}";', '            var response = await signalClient.ExecuteAsync(new RestRequest(requestUrl, Method.Get));`r`n            var content = response.Content ?? string.Empty;`r`n            SignalRequestText.Text = $"{(int?)response.StatusCode ?? 0} {response.StatusDescription} · timeframe={timeframe}";')
$s = $s.Replace('if (!response.IsSuccessStatusCode)', 'if (!response.IsSuccessful)')
$s = $s.Replace('{(int)response.StatusCode}', '{(int?)response.StatusCode ?? 0}')
$s = $s.Replace('            using var document = JsonDocument.Parse(content);`r`n            var root = document.RootElement;', '            var root = JObject.Parse(content);')
$s = $s.Replace('        catch (JsonException)', '        catch (Newtonsoft.Json.JsonException)')
$start = $s.IndexOf('    private static string FindTradeLevelValue')
$end = $s.IndexOf('    private void Render()', $start)
$helpers = @'
    private static string FindTradeLevelValue(JObject root, string name)
    {
        if (TryFindJsonToken(root, out var tradeLevels, "trade_levels") && FindJsonValue(tradeLevels, name) is { } nestedValue)
            return nestedValue;
        return FindJsonValue(root, name) ?? "--";
    }

    private static string FormatSignalItems(JObject root, string propertyName, params string[] fields)
    {
        if (!TryFindJsonToken(root, out var value, propertyName) || value is not JArray array)
            return "--";
        var items = array.Select(item =>
        {
            if (item is not JObject obj) return JTokenToDisplayString(item);
            return string.Join(" · ", fields.Select(field => $"{field}: {FindJsonValue(obj, field) ?? "--"}"));
        }).ToArray();
        return items.Length == 0 ? "--" : string.Join(Environment.NewLine, items);
    }

    private static string FormatConfidenceComponent(JObject root, int index)
    {
        if (!TryFindJsonToken(root, out var components, "confidence_components") || components is not JArray array || array.Count <= index)
            return "--";
        var component = array[index];
        if (component is not JObject obj) return JTokenToDisplayString(component);
        var name = FindJsonValue(obj, "name") ?? "--";
        var score = FindJsonValue(obj, "score") ?? "--";
        var maxScore = FindJsonValue(obj, "max_score") ?? "--";
        var details = FindJsonValue(obj, "details") ?? "--";
        return string.Join(Environment.NewLine, name, $"{score} / {maxScore}", details);
    }

    private static string? FindJsonValue(JToken element, params string[] names) =>
        TryFindJsonToken(element, out var value, names) ? JTokenToDisplayString(value) : null;

    private static bool TryFindJsonToken(JToken element, out JToken result, params string[] names)
    {
        if (element is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    result = property.Value;
                    return true;
                }
            }
            foreach (var property in obj.Properties())
                if (TryFindJsonToken(property.Value, out result, names)) return true;
        }
        else if (element is JArray array)
        {
            foreach (var item in array)
                if (TryFindJsonToken(item, out result, names)) return true;
        }
        result = JValue.CreateNull();
        return false;
    }

    private static string JTokenToDisplayString(JToken token) => token.Type switch
    {
        JTokenType.String => token.Value<string>() ?? "--",
        JTokenType.Integer or JTokenType.Float or JTokenType.Boolean => token.ToString(),
        JTokenType.Null or JTokenType.Undefined => "--",
        _ => token.ToString(Newtonsoft.Json.Formatting.None)
    };

'@
if ($start -lt 0 -or $end -lt 0) { throw 'Could not locate JSON helper block.' }
$s = $s.Substring(0, $start) + $helpers + $s.Substring($end)
[IO.File]::WriteAllText($p, $s)
