using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

[assembly: LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<RadiusApiLambda.JsonContext>))]

namespace RadiusApiLambda;

public class Function
{
    static readonly string RADIUS_AUTH_STATE_TABLE = Environment.GetEnvironmentVariable(nameof(RADIUS_AUTH_STATE_TABLE))
        ?? throw new InvalidOperationException($"{nameof(RADIUS_AUTH_STATE_TABLE)} not set");

    static readonly string DEPLOYED_REGIONS = Environment.GetEnvironmentVariable(nameof(DEPLOYED_REGIONS))
        ?? throw new InvalidOperationException($"{nameof(DEPLOYED_REGIONS)} not set");

    static readonly string AWS_REGION = Environment.GetEnvironmentVariable(nameof(AWS_REGION))
        ?? throw new InvalidOperationException($"{nameof(AWS_REGION)} not set");

    static readonly Dictionary<string, AmazonDynamoDBClient> _ddb_clients = DEPLOYED_REGIONS
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToDictionary(
            r => r,
            r => new AmazonDynamoDBClient(new AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(r)
            })
        );

    static readonly AmazonDynamoDBClient Local = _ddb_clients.GetValueOrDefault(AWS_REGION)
        ?? throw new InvalidOperationException($"No DynamoDB client found for region {AWS_REGION}");

    public static async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest req, ILambdaContext ctx)
    {
        ctx.Logger.LogInformation($"{req.HttpMethod} {req.Resource}");
        try
        {
            return (req.Resource, req.HttpMethod) switch
            {
                ("/users", "GET")               => await ListItems("USER#", req),
                ("/users", "POST")              => await CreateUser(req),
                ("/users/{username}", "GET")    => await GetItem("USER#", req.PathParameters["username"]),
                ("/users/{username}", "PUT")    => await PutUser(req.PathParameters["username"], req),
                ("/users/{username}", "DELETE") => await DeleteItem("USER#", req.PathParameters["username"]),
                ("/nas", "GET")                 => await ListItems("NAS#", req),
                ("/nas/{identifier}", "GET")    => await GetItem("NAS#", req.PathParameters["identifier"]),
                ("/nas/{identifier}", "PUT")    => await PutNas(req.PathParameters["identifier"], req),
                ("/nas/{identifier}", "DELETE") => await DeleteItem("NAS#", req.PathParameters["identifier"]),
                _ => Respond(404, new JsonObject { ["message"] = "Not found" })
            };
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"Unhandled: {ex}");
            return Respond(500, new JsonObject { ["message"] = "Internal server error" });
        }
    }

    // ── LIST ─────────────────────────────────────────────────────────────────

    static async Task<APIGatewayProxyResponse> ListItems(string prefix, APIGatewayProxyRequest req)
    {
        var pageSize = int.TryParse(req.QueryStringParameters?.TryGetValue("limit", out var lm) == true ? lm : null, out var l) ? l : 50;

        // Exclude auto-created MAC bypass records from the user list
        var filter = prefix == "USER#"
            ? "begins_with(PK, :prefix) AND SK = :sk AND attribute_not_exists(is_mac_auth)"
            : "begins_with(PK, :prefix) AND SK = :sk";

        var request = new ScanRequest
        {
            TableName = RADIUS_AUTH_STATE_TABLE,
            FilterExpression = filter,
            ExpressionAttributeValues = new() { [":prefix"] = S(prefix), [":sk"] = S("#CONFIG") },
            Limit = pageSize
        };

        if (req.QueryStringParameters?.TryGetValue("lastKey", out var lastKey) == true && lastKey != null)
            request.ExclusiveStartKey = DecodePageKey(lastKey);

        var resp = await Local.ScanAsync(request);

        return Respond(200, new JsonObject
        {
            ["items"] = new JsonArray([.. resp.Items.Select(i => (JsonNode)ToJson(i, prefix))]),
            ["nextKey"] = resp.LastEvaluatedKey?.Count > 0 ? EncodePageKey(resp.LastEvaluatedKey) : null
        });
    }

    // ── CREATE USER ───────────────────────────────────────────────────────────

    static async Task<APIGatewayProxyResponse> CreateUser(APIGatewayProxyRequest req)
    {
        var body = req.Body != null ? JsonNode.Parse(req.Body)?.AsObject() : null;

        var username = body?["username"]?.GetValue<string>() ?? GenerateId();
        var password = body?["password"]?.GetValue<string>() ?? GenerateSecret();

        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = S($"USER#{username}"),
            ["SK"] = S("#CONFIG"),
            ["user_password"] = S(password)
        };
        MergeStrings(body, item, "vlan", "mfa_key");
        if (body?["groups"] is JsonArray groups) item["groups"] = SS(groups);

        try
        {
            await WriteAll(new PutItemRequest
            {
                TableName = RADIUS_AUTH_STATE_TABLE,
                Item = item,
                ConditionExpression = "attribute_not_exists(PK)"
            });
        }
        catch (ConditionalCheckFailedException)
        {
            return Respond(409, new JsonObject { ["message"] = "User already exists" });
        }

        var result = ToJson(item, "USER#");
        result["password"] = password; // only returned on creation
        return Respond(201, result);
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    static async Task<APIGatewayProxyResponse> GetItem(string prefix, string id)
    {
        var resp = await Local.GetItemAsync(new GetItemRequest
        {
            TableName = RADIUS_AUTH_STATE_TABLE,
            Key = Key(prefix, id)
        });
        return resp.Item?.Count > 0
            ? Respond(200, ToJson(resp.Item, prefix))
            : Respond(404, new JsonObject { ["message"] = "Not found" });
    }

    // ── PUT USER ──────────────────────────────────────────────────────────────

    static async Task<APIGatewayProxyResponse> PutUser(string username, APIGatewayProxyRequest req)
    {
        var body = JsonNode.Parse(req.Body ?? "{}")?.AsObject() ?? new JsonObject();

        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = S($"USER#{username}"),
            ["SK"] = S("#CONFIG")
        };
        if (body["password"]?.GetValue<string>() is string pw) item["user_password"] = S(pw);
        MergeStrings(body, item, "vlan", "mfa_key");
        if (body["groups"] is JsonArray groups) item["groups"] = SS(groups);

        await WriteAll(new PutItemRequest { TableName = RADIUS_AUTH_STATE_TABLE, Item = item });
        return Respond(200, ToJson(item, "USER#"));
    }

    // ── PUT NAS ───────────────────────────────────────────────────────────────

    static async Task<APIGatewayProxyResponse> PutNas(string identifier, APIGatewayProxyRequest req)
    {
        var body = JsonNode.Parse(req.Body ?? "{}")?.AsObject() ?? new JsonObject();

        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = S($"NAS#{identifier}"),
            ["SK"] = S("#CONFIG")
        };
        MergeStrings(body, item, "vlan");
        if (body["session_duration"] is JsonValue sd && sd.TryGetValue<int>(out var dur)) item["session_duration"] = N(dur);
        if (body["auto_allow_users"] is JsonArray patterns) item["auto_allow_users"] = SS(patterns);

        await WriteAll(new PutItemRequest { TableName = RADIUS_AUTH_STATE_TABLE, Item = item });
        return Respond(200, ToJson(item, "NAS#"));
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    static async Task<APIGatewayProxyResponse> DeleteItem(string prefix, string id)
    {
        await WriteAll(new DeleteItemRequest { TableName = RADIUS_AUTH_STATE_TABLE, Key = Key(prefix, id) });
        return Respond(204, null);
    }

    // ── Cross-region fan-out ──────────────────────────────────────────────────

    static Task<PutItemResponse[]> WriteAll(PutItemRequest req) =>
        Task.WhenAll(_ddb_clients.Select(kv => kv.Value.PutItemAsync(req)));

    static Task<DeleteItemResponse[]> WriteAll(DeleteItemRequest req) =>
        Task.WhenAll(_ddb_clients.Select(kv => kv.Value.DeleteItemAsync(req)));

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string GenerateId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();

    static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    static Dictionary<string, AttributeValue> Key(string prefix, string id) => new()
    {
        ["PK"] = S($"{prefix}{id}"),
        ["SK"] = S("#CONFIG")
    };

    static AttributeValue S(string v) => new() { S = v };
    static AttributeValue N(int v) => new() { N = v.ToString() };
    static AttributeValue SS(JsonArray arr) => new()
    {
        SS = [.. arr.Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0)]
    };

    static void MergeStrings(JsonObject? body, Dictionary<string, AttributeValue> item, params string[] keys)
    {
        if (body == null) return;
        foreach (var key in keys)
            if (body[key]?.GetValue<string>() is string v)
                item[key] = S(v);
    }

    static JsonObject ToJson(Dictionary<string, AttributeValue> item, string prefix)
    {
        var obj = new JsonObject();
        if (item.TryGetValue("PK", out var pk) && pk.S != null)
            obj[prefix == "USER#" ? "username" : "identifier"] = pk.S[prefix.Length..];
        foreach (var (key, val) in item)
        {
            if (key is "PK" or "SK" or "user_password" or "is_mac_auth") continue;
            if (val.S != null) obj[key] = val.S;
            else if (val.N != null) obj[key] = decimal.Parse(val.N);
            else if (val.SS?.Count > 0) obj[key] = new JsonArray([.. val.SS.Select(s => (JsonNode)s!)]);
        }
        return obj;
    }

    static Dictionary<string, AttributeValue>? DecodePageKey(string encoded)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize(json, JsonContext.Default.DictionaryStringString)
                ?.ToDictionary(k => k.Key, k => S(k.Value));
        }
        catch { return null; }
    }

    static string EncodePageKey(Dictionary<string, AttributeValue> key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                key.ToDictionary(k => k.Key, k => k.Value.S ?? k.Value.N ?? ""),
                JsonContext.Default.DictionaryStringString)));

    static APIGatewayProxyResponse Respond(int status, JsonObject? body) => new()
    {
        StatusCode = status,
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        Body = body?.ToJsonString() ?? ""
    };
}

[JsonSerializable(typeof(APIGatewayProxyRequest))]
[JsonSerializable(typeof(APIGatewayProxyResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
public partial class JsonContext : JsonSerializerContext { }
