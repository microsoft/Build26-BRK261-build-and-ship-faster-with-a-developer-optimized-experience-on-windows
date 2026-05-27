using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MarkItDownApp.Services;

/// <summary>
/// HTTP client for the MarkItDown REST API running in the container.
/// </summary>
public sealed class MarkItDownClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "http://localhost:8000/api/v1";

    public MarkItDownClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>
    /// Checks if the service is healthy.
    /// </summary>
    public async Task<bool> CheckHealthAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the list of supported file extensions.
    /// </summary>
    public async Task<string[]> GetFormatsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/formats");
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<FormatsResponse>();
            return result?.Extensions ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarkItDownClient] GetFormats error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Converts a local file to Markdown via the container API.
    /// </summary>
    public async Task<ConvertResult> ConvertFileAsync(string filePath)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(filePath);
        var fileName = Path.GetFileName(filePath);

        var streamContent = new StreamContent(fileStream);
        form.Add(streamContent, "file", fileName);

        Debug.WriteLine($"[MarkItDownClient] Converting: {fileName} ({fileStream.Length} bytes)");

        var response = await _httpClient.PostAsync($"{BaseUrl}/convert", form);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Conversion failed with status {(int)response.StatusCode}: {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<ConvertResult>();
        return result ?? throw new InvalidOperationException("Empty response from conversion API");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public record ConvertResult(
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("content_type")] string? ContentType,
    [property: JsonPropertyName("bytes")] int Bytes,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("title")] string? Title
);

public record FormatsResponse(
    [property: JsonPropertyName("extensions")] string[] Extensions
);
