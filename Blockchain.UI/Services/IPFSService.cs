using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Forms;

namespace Blockchain.UI.Services
{
    public class IpfsService
    {
        private readonly HttpClient _httpClient;

        public IpfsService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> UploadFileAsync(IBrowserFile file)
        {
            try
            {
                using var content = new MultipartFormDataContent();

                var fileContent = new StreamContent(file.OpenReadStream(50 * 1024 * 1024));
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

                content.Add(fileContent, "file", "upload.bin");

                var response = await _httpClient.PostAsync("http://127.0.0.1:5001/api/v0/add", content);
                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(jsonResponse);

                if (document.RootElement.TryGetProperty("Hash", out var hashElement))
                {
                    return hashElement.GetString() ?? throw new Exception("IPFS не вернул CID.");
                }

                throw new Exception("Неверный формат ответа от IPFS.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка загрузки в IPFS: {ex.Message}");
            }
        }
    }
}