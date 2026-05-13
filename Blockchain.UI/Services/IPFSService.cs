using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;

namespace Blockchain.UI.Services
{
    public class IpfsService
    {
        private readonly HttpClient _httpClient;
        private readonly string _ipfsApiUrl;
        private readonly string _ipfsGatewayUrl;

        public IpfsService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _ipfsApiUrl = NormalizeUrl(configuration["IpfsApiUrl"] ?? "http://127.0.0.1:5001/api/v0/add");
            _ipfsGatewayUrl = NormalizeGatewayUrl(configuration["IpfsGatewayUrl"] ?? "http://127.0.0.1:8080/ipfs");
        }

        public async Task<string> UploadFileAsync(IBrowserFile file)
        {
            try
            {
                using var content = new MultipartFormDataContent();

                var fileContent = new StreamContent(file.OpenReadStream(50 * 1024 * 1024));
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

                content.Add(fileContent, "file", "upload.bin");

                var response = await _httpClient.PostAsync(_ipfsApiUrl, content);
                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(jsonResponse);

                if (document.RootElement.TryGetProperty("Hash", out var hashElement))
                {
                    string cid = hashElement.GetString() ?? throw new Exception("IPFS did not return a CID.");
                    await VerifyCidAvailableAsync(cid);
                    return cid;
                }

                throw new Exception("Invalid IPFS response format.");
            }
            catch (Exception ex)
            {
                throw new Exception($"IPFS upload failed: {ex.Message}");
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "http://127.0.0.1:5001/api/v0/add";
            }

            return url.Trim();
        }

        private static string NormalizeGatewayUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "http://127.0.0.1:8080/ipfs";
            }

            return url.Trim().TrimEnd('/');
        }

        private async Task VerifyCidAvailableAsync(string cid)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_ipfsGatewayUrl}/{cid}");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"CID was uploaded but is not reachable through the IPFS gateway ({(int)response.StatusCode}).");
            }
        }
    }
}
