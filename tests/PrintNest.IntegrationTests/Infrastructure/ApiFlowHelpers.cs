using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace PrintNest.IntegrationTests.Infrastructure;

internal static class ApiFlowHelpers
{
    private const string EmptyBodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed record DeviceIdentity(string StoreId, string DeviceId, string SharedSecret);
    internal sealed record JobIdentity(Guid JobId, string Otp);
    internal sealed record ReleaseIdentity(Guid JobId, string FileToken);
    internal sealed record StaffAuthSession(string AccessToken, string Role, string? StoreId);

    public static async Task<StaffAuthSession> LoginAsStaffAsync(
        HttpClient client,
        string username,
        string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/staff/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = payload.GetProperty("accessToken").GetString();
        accessToken.Should().NotBeNullOrWhiteSpace();

        var user = payload.GetProperty("user");
        var role = user.GetProperty("role").GetString()!;
        var storeId = user.TryGetProperty("storeId", out var storeIdNode) ? storeIdNode.GetString() : null;

        return new StaffAuthSession(accessToken!, role, storeId);
    }

    public static async Task<Guid> CreateDraftJobAsync(HttpClient client)
    {
        using var createResponse = await client.PostAsJsonAsync("/api/v1/public/printjobs", new
        {
            fileName = "integration.pdf",
            fileSizeBytes = BuildFakePdfBytes().Length,
            contentType = "application/pdf"
        });
        createResponse.EnsureSuccessStatusCode();

        var createPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        return createPayload.GetProperty("jobId").GetGuid();
    }

    public static async Task<DeviceIdentity> RegisterStoreAndDeviceAsync(
        HttpClient client,
        string _legacyUnusedAdminAuthValue,
        string? storeId = null,
        string? deviceId = null)
    {
        storeId ??= $"store_{Guid.NewGuid():N}";
        deviceId ??= $"dev_{Guid.NewGuid():N}";
        var admin = await LoginAsStaffAsync(client, "admin", "integration-admin-pass-123");
        var adminAccessToken = admin.AccessToken;

        using var storeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/stores");
        storeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminAccessToken);
        storeRequest.Content = JsonContent.Create(new
        {
            storeId,
            name = "Integration Store",
            address = "123 Test Street",
            latitude = 17.4486,
            longitude = 78.3908
        });

        using var storeResponse = await client.SendAsync(storeRequest);
        storeResponse.EnsureSuccessStatusCode();

        using var registerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/devices");
        registerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminAccessToken);
        registerRequest.Content = JsonContent.Create(new { deviceId, storeId });

        using var registerResponse = await client.SendAsync(registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        var registerPayload = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sharedSecret = registerPayload.GetProperty("sharedSecret").GetString();
        sharedSecret.Should().NotBeNullOrWhiteSpace();

        return new DeviceIdentity(storeId, deviceId, sharedSecret!);
    }

    public static async Task<JobIdentity> CreatePaidJobWithOtpAsync(HttpClient client)
    {
        var pdfBytes = BuildFakePdfBytes();

        using var createResponse = await client.PostAsJsonAsync("/api/v1/public/printjobs", new
        {
            fileName = "integration.pdf",
            fileSizeBytes = pdfBytes.Length,
            contentType = "application/pdf"
        });
        createResponse.EnsureSuccessStatusCode();

        var createPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = createPayload.GetProperty("jobId").GetGuid();
        var uploadUrl = createPayload.GetProperty("upload").GetProperty("url").GetString();
        uploadUrl.Should().NotBeNullOrWhiteSpace();
        uploadUrl = NormalizeToHttp(uploadUrl!);

        using (var uploadClient = new HttpClient())
        using (var uploadContent = new ByteArrayContent(pdfBytes))
        {
            uploadContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            using var uploadResponse = await uploadClient.PutAsync(uploadUrl, uploadContent);
            uploadResponse.EnsureSuccessStatusCode();
        }

        using var finalizeResponse = await client.PostAsJsonAsync($"/api/v1/public/printjobs/{jobId}/finalize", new { sha256 = "abc123" });
        finalizeResponse.EnsureSuccessStatusCode();

        using var quoteResponse = await client.PostAsJsonAsync($"/api/v1/public/printjobs/{jobId}/quote", new
        {
            copies = 1,
            color = "BW"
        });
        quoteResponse.EnsureSuccessStatusCode();

        using var payResponse = await client.PostAsJsonAsync($"/api/v1/public/printjobs/{jobId}/pay-mock", new { });
        payResponse.EnsureSuccessStatusCode();

        using var otpResponse = await client.PostAsJsonAsync($"/api/v1/public/printjobs/{jobId}/otp/generate", new { });
        otpResponse.EnsureSuccessStatusCode();

        var otpPayload = await otpResponse.Content.ReadFromJsonAsync<JsonElement>();
        var otp = otpPayload.GetProperty("otp").GetString();
        otp.Should().NotBeNullOrWhiteSpace();

        return new JobIdentity(jobId, otp!);
    }

    public static async Task<ReleaseIdentity> ReleaseByOtpAsync(
        HttpClient client,
        DeviceIdentity device,
        string otp,
        string? storeId = null)
    {
        var request = CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            device.DeviceId,
            device.SharedSecret,
            new { otp, storeId = storeId ?? device.StoreId });

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = payload.GetProperty("jobId").GetGuid();
        var fileToken = payload.GetProperty("fileToken").GetProperty("token").GetString();
        fileToken.Should().NotBeNullOrWhiteSpace();

        return new ReleaseIdentity(jobId, fileToken!);
    }

    public static HttpRequestMessage CreateSignedJsonRequest(
        HttpMethod method,
        string path,
        string deviceId,
        string sharedSecret,
        object body,
        long? unixTimestamp = null,
        string? forcedSignature = null)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var timestamp = unixTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = forcedSignature ?? ComputeSignature(sharedSecret, timestamp, method, path, bodyBytes);

        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("X-Device-Id", deviceId);
        request.Headers.Add("X-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Signature", signature);

        return request;
    }

    public static HttpRequestMessage CreateSignedRequest(
        HttpMethod method,
        string path,
        string deviceId,
        string sharedSecret,
        string? bearerToken = null,
        long? unixTimestamp = null,
        string? forcedSignature = null)
    {
        var bodyBytes = Array.Empty<byte>();
        var timestamp = unixTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = forcedSignature ?? ComputeSignature(sharedSecret, timestamp, method, path, bodyBytes);

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Device-Id", deviceId);
        request.Headers.Add("X-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Signature", signature);

        if (!string.IsNullOrEmpty(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        return request;
    }

    public static HttpRequestMessage CreateStaffRequest(
        HttpMethod method,
        string path,
        string accessToken,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    public static async Task<string> GetJobStatusAsync(HttpClient client, Guid jobId)
    {
        using var response = await client.GetAsync($"/api/v1/public/printjobs/{jobId}");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("status").GetString()!;
    }

    public static async Task<string?> GetErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("error", out var error))
            return null;

        if (!error.TryGetProperty("code", out var code))
            return null;

        return code.GetString();
    }

    private static byte[] BuildFakePdfBytes()
    {
        const string minimalPdf = """
            %PDF-1.4
            1 0 obj
            << /Type /Catalog /Pages 2 0 R >>
            endobj
            2 0 obj
            << /Type /Pages /Kids [3 0 R] /Count 1 >>
            endobj
            3 0 obj
            << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>
            endobj
            trailer
            << /Root 1 0 R >>
            %%EOF
            """;

        return Encoding.UTF8.GetBytes(minimalPdf);
    }

    private static string ComputeSignature(
        string sharedSecret,
        long unixTimestamp,
        HttpMethod method,
        string path,
        byte[] bodyBytes)
    {
        var bodyHash = bodyBytes.Length == 0
            ? EmptyBodyHash
            : Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

        var message = $"{unixTimestamp}\n{method.Method.ToUpperInvariant()}\n{path}\n{bodyHash}";
        var secret = Convert.FromBase64String(sharedSecret);

        using var hmac = new HMACSHA256(secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeToHttp(string url)
    {
        var uri = new Uri(url);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttp,
            Port = uri.IsDefaultPort ? 80 : uri.Port
        };
        return builder.Uri.ToString();
    }
}
