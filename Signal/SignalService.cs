using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace XYN.Signal;

public class SignalService
{
    private static readonly RestClient client = new();

    public async Task<SignalResponse?> GetSignalAsync(string url)
    {
        var response = await client.ExecuteAsync(new RestRequest(url, Method.Get));
        if (!response.IsSuccessful || string.IsNullOrWhiteSpace(response.Content)) return null;
        return JsonConvert.DeserializeObject<SignalResponse>(response.Content);
    }

    public void DeserializeSignalResponse(string json)
    {
        var signalResponse = JsonConvert.DeserializeObject<SignalResponse>(json);
    }

    public void GetSignal(string json)
    {
        var obj = JObject.Parse(json);
    }

    private static string FormatFactors(IEnumerable<Factor> factors) =>
        string.Join(Environment.NewLine + Environment.NewLine,
            factors.Select(f => $"• {f.Label} [сила {f.Strength}]{Environment.NewLine}  {f.Explanation}"));
}
