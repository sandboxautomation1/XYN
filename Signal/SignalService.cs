using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices.JavaScript;
using System.Text;

namespace XYN.Signal
{
    public class SignalService
    {
        private static readonly HttpClient _client = new();

        public async Task<SignalResponse?> GetSignalAsync(string url)
        {
            return await _client.GetFromJsonAsync<SignalResponse>(url);
        }


        public void DeserializeSignalResponse(string json)
        {
            var signalResponse = System.Text.Json.JsonSerializer.Deserialize<SignalResponse>(json);
            // You can now use the signalResponse object as needed
        }


        public void GetSignal(string json)
        {
            var obj = JObject.Parse(json);
        }


        private static string FormatFactors(IEnumerable<Factor> factors) =>
            string.Join(Environment.NewLine + Environment.NewLine,
                factors.Select(f => $"• {f.Label} [сила {f.Strength}]{Environment.NewLine}  {f.Explanation}"));
    }
}
