using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RaidZero.BackendApi
{
    /// <summary>One HTTP request to the backend, with every header it needs already set.</summary>
    public sealed class BackendRequest
    {
        public BackendRequest(string method, string path, string body, IReadOnlyDictionary<string, string> headers)
        {
            Method = method;
            Path = path;
            Body = body;
            Headers = headers;
        }

        public string Method { get; }

        /// <summary>Starts with a slash, e.g. /v1/server/raids/{id}/join.</summary>
        public string Path { get; }

        /// <summary>JSON, or null for no body. Sent exactly as is: the backend's idempotency check hashes it.</summary>
        public string Body { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }
    }

    public sealed class BackendResponse
    {
        public BackendResponse(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body ?? "";
        }

        public int StatusCode { get; }

        public string Body { get; }
    }

    /// <summary>
    /// Sends requests. Throws when no answer arrives (network down, timeout); any answer, error or not, is a
    /// <see cref="BackendResponse"/>. Tests replace this to simulate failures.
    /// </summary>
    public interface IBackendTransport : IDisposable
    {
        Task<BackendResponse> SendAsync(BackendRequest request, CancellationToken cancellationToken);
    }

    /// <summary>The real transport, over HttpClient.</summary>
    public sealed class HttpClientTransport : IBackendTransport
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public HttpClientTransport(string baseUrl, TimeSpan timeout)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = timeout };
        }

        public async Task<BackendResponse> SendAsync(BackendRequest request, CancellationToken cancellationToken)
        {
            using (var message = new HttpRequestMessage(new HttpMethod(request.Method), _baseUrl + request.Path))
            {
                if (request.Body != null)
                {
                    message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
                }

                foreach (var header in request.Headers)
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using (var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new BackendResponse((int)response.StatusCode, body);
                }
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
