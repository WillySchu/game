using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// JSON in and out, by hand. Reflection-based serialization would work in the editor and then fail in an
    /// IL2CPP build once code stripping removes the setters it needs, so nothing here uses it.
    /// </summary>
    internal static class Wire
    {
        /// <summary>Parses without Newtonsoft's habit of turning date-looking strings into DateTime.</summary>
        public static JToken Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                return JToken.ReadFrom(reader);
            }
        }

        /// <summary>The body as an object, or null if it's empty or not a JSON object.</summary>
        public static JObject TryParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Compact(JToken token) => token.ToString(Formatting.None);

        /// <summary>The item tree's wire shape, matching the backend: optional fields are left out when unset.</summary>
        public static JObject ToJson(ItemNode node)
        {
            var json = new JObject
            {
                ["id"] = node.Id,
                ["def_id"] = node.DefId,
            };

            if (node.Slot != null)
            {
                json["slot"] = node.Slot;
            }

            if (node.X.HasValue)
            {
                json["x"] = node.X.Value;
            }

            if (node.Y.HasValue)
            {
                json["y"] = node.Y.Value;
            }

            json["rotated"] = node.Rotated;
            json["quantity"] = node.Quantity;
            if (node.Durability.HasValue)
            {
                json["durability"] = node.Durability.Value;
            }

            var children = new JArray();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (child != null)
                    {
                        children.Add(ToJson(child));
                    }
                }
            }

            json["children"] = children;
            return json;
        }

        public static ItemNode ToItemNode(JToken token)
        {
            if (!(token is JObject json))
            {
                throw new FormatException("Expected an item object.");
            }

            var node = new ItemNode
            {
                Id = (string)json["id"],
                DefId = (string)json["def_id"],
                Slot = (string)json["slot"],
                X = (int?)json["x"],
                Y = (int?)json["y"],
                Rotated = (bool?)json["rotated"] ?? false,
                Quantity = (int?)json["quantity"] ?? 1,
                Durability = (double?)json["durability"],
            };

            if (json["children"] is JArray children)
            {
                foreach (var child in children)
                {
                    node.Children.Add(ToItemNode(child));
                }
            }

            return node;
        }

        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    hex.Append(b.ToString("x2"));
                }

                return hex.ToString();
            }
        }
    }

    internal enum CallStatus
    {
        /// <summary>A 2xx. Done.</summary>
        Succeeded,

        /// <summary>The backend understood the request and said no. Sending it again would get the same answer.</summary>
        Rejected,

        /// <summary>No answer, or an answer that may change: network trouble, a 5xx, or a configuration problem.</summary>
        Retry,
    }

    /// <summary>What came back from one attempt, sorted into what to do next.</summary>
    internal sealed class CallResult
    {
        private CallResult(CallStatus status, int statusCode, string body, string error, string message)
        {
            Status = status;
            StatusCode = statusCode;
            Body = body;
            Error = error;
            Message = message;
        }

        public CallStatus Status { get; }

        /// <summary>0 when no answer arrived.</summary>
        public int StatusCode { get; }

        public string Body { get; }

        /// <summary>The backend's error code, such as "ticket_expired", or "backend_unreachable".</summary>
        public string Error { get; }

        public string Message { get; }

        /// <summary>
        /// 401 and 403 mean a wrong server token, and catalog_mismatch a server built from a different catalog.
        /// Retrying won't fix either until someone does, but neither is a verdict on the report itself, so
        /// reports wait rather than being dropped. These are logged as errors instead of warnings.
        /// </summary>
        public bool IsConfigurationProblem => StatusCode == 401 || StatusCode == 403 || Error == "catalog_mismatch";

        public static CallResult Unreachable(Exception exception) =>
            new CallResult(CallStatus.Retry, 0, "", "backend_unreachable", exception.GetType().Name + ": " + exception.Message);

        public static CallResult From(BackendResponse response)
        {
            var json = Wire.TryParseObject(response.Body);
            var error = (string)json?["error"];
            var message = (string)json?["message"];
            var code = response.StatusCode;

            CallStatus status;
            if (code >= 200 && code < 300)
            {
                status = CallStatus.Succeeded;
            }
            else if (code == 401 || code == 403 || code == 408 || code == 429 || code >= 500 || error == "catalog_mismatch")
            {
                status = CallStatus.Retry;
            }
            else
            {
                status = CallStatus.Rejected;
            }

            return new CallResult(status, code, response.Body, error ?? (status == CallStatus.Succeeded ? null : "http_" + code), message ?? "");
        }

        public override string ToString() =>
            StatusCode == 0 ? $"{Error} ({Message})" : $"HTTP {StatusCode} {Error}{(Message.Length > 0 ? ": " + Message : "")}";
    }
}
