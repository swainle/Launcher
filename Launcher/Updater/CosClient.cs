using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Updater;

internal sealed class CosClient
    {
        private readonly HttpClient _client;
        private readonly Uri _bucket;
        private readonly string _secretID;
        private readonly string _secretKey;
        private readonly string _sessionToken;

        public CosClient(HttpClient client, string bucketURL, string secretID, string secretKey, string sessionToken)
        {
            this._client = client;
            this._bucket = new Uri(bucketURL);
            this._secretID = secretID;
            this._secretKey = secretKey;
            this._sessionToken = sessionToken;
        }

        public async Task<bool> PutFileIfChangedAsync(string key, string path, string sha256, CancellationToken ct)
        {
            using var head = this.NewRequest(HttpMethod.Head, key);
            this.Sign(head);
            using var headResponse = await this._client.SendAsync(head, ct);
            if (headResponse.IsSuccessStatusCode &&
                headResponse.Headers.TryGetValues("x-cos-meta-sha256", out var hashes))
            {
                foreach (var hash in hashes)
                {
                    if (hash == sha256)
                    {
                        return true;
                    }
                }
            }

            if (!headResponse.IsSuccessStatusCode && headResponse.StatusCode != HttpStatusCode.NotFound)
            {
                headResponse.EnsureSuccessStatusCode();
            }

            using var request = this.NewRequest(HttpMethod.Put, key);
            request.Headers.TryAddWithoutValidation("x-cos-meta-sha256", sha256);
            request.Content = new StreamContent(File.OpenRead(path));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            this.Sign(request);
            using var response = await this._client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return false;
        }

        public async Task PutBytesAsync(string key, byte[] value, string contentType, CancellationToken ct)
        {
            using var request = this.NewRequest(HttpMethod.Put, key);
            request.Content = new ByteArrayContent(value);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            this.Sign(request);
            using var response = await this._client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }

        public async Task<ReleaseIndex> GetIndexAsync(string key, CancellationToken ct)
        {
            using var request = this.NewRequest(HttpMethod.Get, key);
            this.Sign(request);
            using var response = await this._client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ReleaseIndex();
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new UpdateService.LimitedStream(stream, UpdateService.MaxManifestBytes);
            return await JsonSerializer.DeserializeAsync(limited, UpdaterJsonContext.Default.ReleaseIndex, ct) ??
                new ReleaseIndex();
        }

        public async Task<VersionManifest?> GetManifestAsync(string key, CancellationToken ct)
        {
            using var request = this.NewRequest(HttpMethod.Get, key);
            this.Sign(request);
            using var response = await this._client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new UpdateService.LimitedStream(stream, UpdateService.MaxManifestBytes);
            return await JsonSerializer.DeserializeAsync(limited, UpdaterJsonContext.Default.VersionManifest, ct) ??
                throw new InvalidDataException("remote manifest is empty");
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string key)
        {
            var url = new UriBuilder(this._bucket);
            url.Path = "/" + key;
            url.Query = string.Empty;
            url.Fragment = string.Empty;
            return new HttpRequestMessage(method, url.Uri);
        }

        private void Sign(HttpRequestMessage request)
        {
            if (this._sessionToken.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("x-cos-security-token", this._sessionToken);
            }

            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            headers.Add("host", request.RequestUri!.Authority);
            foreach (var header in request.Headers)
            {
                if (header.Key.StartsWith("x-cos-", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add(header.Key.ToLowerInvariant(), string.Join(",", header.Value));
                }
            }

            if (request.Content?.Headers.ContentType is not null)
            {
                headers.Add("content-type", request.Content.Headers.ContentType.ToString());
            }

            var headerList = string.Join(";", headers.Keys);
            var formatted = new List<string>();
            foreach (var header in headers)
            {
                formatted.Add(Uri.EscapeDataString(header.Key) + "=" + Uri.EscapeDataString(header.Value));
            }

            var keyTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signTime = keyTime + 3600;
            var time = keyTime + ";" + signTime;
            var httpString = request.Method.Method.ToLowerInvariant() + "\n" + request.RequestUri.AbsolutePath + "\n\n" + string.Join("&", formatted) + "\n";
            var stringToSign = "sha1\n" + time + "\n" + Sha1Hex(Encoding.UTF8.GetBytes(httpString)) + "\n";
            var signKey = HmacHex(this._secretKey, time);
            var signature = HmacHex(signKey, stringToSign);
            var authorization = "q-sign-algorithm=sha1&q-ak=" + this._secretID +
                "&q-sign-time=" + time + "&q-key-time=" + time +
                "&q-header-list=" + headerList + "&q-url-param-list=&q-signature=" + signature;
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        private static string Sha1Hex(byte[] value)
        {
            return Convert.ToHexStringLower(SHA1.HashData(value));
        }

        private static string HmacHex(string key, string value)
        {
            return Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
        }
    }
